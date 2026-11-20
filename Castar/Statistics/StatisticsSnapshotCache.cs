using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Statistics;

/// <summary>Disposable derived chart data; never a source of financial truth.</summary>
internal sealed class StatisticsSnapshotCache
{
    internal const int MaxEntries = 16;
    internal const int MaxPoints = 100_000;
    private readonly object gate = new();
    private readonly Dictionary<Key, Cached> completed = new();
    private readonly Dictionary<(Key Key, long Version), Flight> pending = new();
    private readonly SemaphoreSlim admissions = new(16);
    private readonly SemaphoreSlim builders = new(2);
    private long version;
    private long access;
    private long points;

    internal void Invalidate()
    {
        Flight[] obsolete;
        lock (gate)
        {
            version++;
            completed.Clear();
            points = 0;
            obsolete = pending.Values.ToArray();
            pending.Clear();
        }
        foreach (var flight in obsolete) Cancel(flight);
    }

    internal async Task<StatisticsSnapshot> GetAsync(DateTime start, DateTime finish, string currency,
        bool includeOpening, Func<CancellationToken, Task<StatisticsSnapshot>> factory, CancellationToken token,
        string scope = "")
    {
        token.ThrowIfCancellationRequested();
        var key = new Key(start, finish, currency, includeOpening, scope);
        Flight? shared;
        long generation;
        lock (gate)
        {
            if (TryGet(key, out var cached)) return cached!;
            generation = version;
            if (pending.TryGetValue((key, generation), out shared)) shared.Waiters++;
        }
        if (shared is not null) return await WaitAsync(key, generation, shared, token).ConfigureAwait(false);

        // Bound queued work as well as completed entries; duplicate readers share admission.
        await admissions.WaitAsync(token).ConfigureAwait(false);
        var created = false;
        lock (gate)
        {
            generation = version;
            if (TryGet(key, out var cached))
            {
                admissions.Release();
                return cached!;
            }
            if (!pending.TryGetValue((key, generation), out shared))
            {
                shared = new Flight();
                pending.Add((key, generation), shared);
                created = true;
            }
            shared.Waiters++;
        }
        if (!created) admissions.Release();
        else _ = BuildAsync(key, generation, factory, shared!);
        return await WaitAsync(key, generation, shared!, token).ConfigureAwait(false);
    }

    private async Task<StatisticsSnapshot> WaitAsync(Key key, long generation, Flight flight, CancellationToken token)
    {
        try
        {
            var snapshot = await flight.Source.Task.WaitAsync(token).ConfigureAwait(false);
            lock (gate)
                if (version != generation) throw new StatisticsCacheInvalidatedException();
            return snapshot;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && IsObsolete(generation))
        {
            throw new StatisticsCacheInvalidatedException();
        }
        finally
        {
            var cancel = false;
            lock (gate)
            {
                flight.Waiters--;
                if (flight.Waiters == 0 && !flight.Source.Task.IsCompleted)
                {
                    RemoveFlight(key, generation, flight);
                    cancel = true;
                }
            }
            if (cancel) Cancel(flight);
        }
    }

    private bool IsObsolete(long generation)
    {
        lock (gate) return version != generation;
    }

    private void RemoveFlight(Key key, long generation, Flight flight)
    {
        if (pending.TryGetValue((key, generation), out var current) && ReferenceEquals(current, flight))
            pending.Remove((key, generation));
    }

    private static void Cancel(Flight flight)
    {
        try { flight.Deadline.Cancel(); }
        catch (ObjectDisposedException) { } // Producer completed between removal and cancellation.
    }

    private bool TryGet(Key key, out StatisticsSnapshot? snapshot)
    {
        snapshot = null;
        if (!completed.TryGetValue(key, out var item)) return false;
        if (item.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            points -= item.Points;
            completed.Remove(key);
            return false;
        }
        item.Access = ++access;
        snapshot = item.Snapshot;
        return true;
    }

    private async Task BuildAsync(Key key, long generation,
        Func<CancellationToken, Task<StatisticsSnapshot>> factory, Flight flight)
    {
        var deadline = flight.Deadline;
        var source = flight.Source;
        var entered = false;
        try
        {
            await builders.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            var snapshot = await factory(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                var active = flight.Waiters > 0 && pending.TryGetValue((key, generation), out var current)
                    && ReferenceEquals(current, flight);
                if (active && version == generation && CanStore(snapshot))
                {
                    var size = PointCount(snapshot);
                    if (size <= MaxPoints)
                    {
                        while (completed.Count >= MaxEntries || points + size > MaxPoints)
                        {
                            var oldest = completed.MinBy(pair => pair.Value.Access);
                            points -= oldest.Value.Points;
                            completed.Remove(oldest.Key);
                        }
                        var lifetime = key.Currency.Length == 0 ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(2);
                        completed.Add(key, new(snapshot, size, ++access, DateTimeOffset.UtcNow + lifetime));
                        points += size;
                    }
                }
                RemoveFlight(key, generation, flight);
                if (!active || version != generation) source.TrySetCanceled();
                else source.TrySetResult(snapshot);
            }
        }
        catch (OperationCanceledException)
        {
            lock (gate) RemoveFlight(key, generation, flight);
            source.TrySetCanceled();
        }
        catch (Exception error)
        {
            lock (gate) RemoveFlight(key, generation, flight);
            source.TrySetException(error);
            // All consumers may have canceled their waits; observe a shared failure here too.
            _ = source.Task.Exception;
        }
        finally
        {
            if (entered) builders.Release();
            admissions.Release();
            deadline.Dispose();
        }
    }

    private static long PointCount(StatisticsSnapshot snapshot) => snapshot.Currencies.Sum(row =>
        (long)row.DailyPoints.Count + row.RemainingPoints.Count + row.MonthlyPoints.Count + 1);

    private static bool CanStore(StatisticsSnapshot snapshot) => snapshot.HasRecords
        && snapshot.UnconvertedIncomes == 0 && snapshot.UnconvertedExpenses == 0
        && snapshot.Currencies.Count > 0 && snapshot.Currencies.All(row => row.RemainingWarning.Length == 0
            && row.Income.HasValue && row.Expenses.HasValue && row.Balance.HasValue
            && row.DailyPoints.All(point => point.Income.HasValue && point.Expenses.HasValue)
            && row.RemainingPoints.All(point => point.Amount.HasValue)
            && row.MonthlyPoints.All(point => point.Income.HasValue && point.Expenses.HasValue));

    private readonly record struct Key(DateTime Start, DateTime Finish, string Currency, bool IncludeOpening, string Scope);
    private sealed class Flight
    {
        internal TaskCompletionSource<StatisticsSnapshot> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource Deadline { get; } = new(TimeSpan.FromSeconds(60));
        internal int Waiters { get; set; }
    }
    private sealed class Cached(StatisticsSnapshot snapshot, long points, long access, DateTimeOffset expiresAt)
    {
        internal StatisticsSnapshot Snapshot { get; } = snapshot;
        internal long Points { get; } = points;
        internal long Access { get; set; } = access;
        internal DateTimeOffset ExpiresAt { get; } = expiresAt;
    }
}
