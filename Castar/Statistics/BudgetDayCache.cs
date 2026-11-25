using Castar.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Statistics;

/// <summary>Complete calendar days and opening operations. Cached amounts retain native precision and rate dates.</summary>
internal sealed class BudgetDayCache(string? path)
{
    private const int MaxDays = 366 * 30;
    private const int MaxOpenings = 4;
    private const int MaxRows = 160_000;
    private const int MaxFileBytes = 24 * 1024 * 1024;
    private const int FormatVersion = 1;
    private readonly SemaphoreSlim gate = new(1);
    private readonly object revisions = new();
    private readonly Dictionary<LoadKey, LoadFlight> loads = new();
    private long generation;
    private CacheState? state;

    internal async Task<BudgetOperationDto[]> GetAsync(string identity, DateOnly first, DateOnly last,
        bool includeOpening, Func<DateOnly, DateOnly, bool, CancellationToken, Task<BudgetOperationDto[]>> load,
        Func<BudgetOperationDto, DateOnly?> reportDay, Func<bool> stillCurrent, CancellationToken token)
    {
        long version;
        lock (revisions)
            version = Interlocked.Read(ref generation);
        using var reads = new LoadReads(this, identity, version, load, token);
        if (last.DayNumber - first.DayNumber >= MaxDays || last < first)
            return await LoadUncachedAsync().ConfigureAwait(false);

        CacheState basis;
        Range[] missing;
        bool loadOpening;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            basis = await ReadAsync(identity, token).ConfigureAwait(false);
            CheckVersion(version, stillCurrent);
            var start = first;
            if (includeOpening && basis.DirtyFrom is { } dirty && dirty < first
                && last.DayNumber - dirty.DayNumber < MaxDays && FindOpening(basis, dirty) is not null)
                start = dirty;
            missing = GetMissingRanges(basis.Days, start, last).ToArray();
            if (missing.Length > 4)
                missing = [new Range(missing[0].First, missing[^1].Last)];
            loadOpening = includeOpening && start == first && FindOpening(basis, first) is null;
            if (!loadOpening && missing.Length == 0)
                return Collect(basis, first, last, includeOpening);
        }
        finally
        {
            gate.Release();
        }

        var loaded = new Dictionary<DateOnly, BudgetOperationDto[]>();
        BudgetOperationDto[]? opening = null;
        Range? openingRange = null;
        if (loadOpening)
        {
            // Keep the opening and the day at its boundary in the same server transaction.
            var range = missing.Length > 0 && missing[0].First == first ? missing[0] : new Range(first, first);
            var rows = await reads.Start(range, includeOpening: true).WaitAsync(token).ConfigureAwait(false);
            opening = rows.Where(row => row.IsOpening).ToArray();
            openingRange = range;
            if (!AddDays(loaded, range, rows, reportDay))
                return await LoadUncachedAsync().ConfigureAwait(false);
            if (missing.Length > 0 && missing[0] == range)
                missing = missing[1..];
        }

        foreach (var range in missing)
        {
            var rows = await reads.Start(range, includeOpening: false).WaitAsync(token).ConfigureAwait(false);
            if (!AddDays(loaded, range, rows, reportDay))
                return await LoadUncachedAsync().ConfigureAwait(false);
        }

        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cached = await ReadAsync(identity, token).ConfigureAwait(false);
            CheckVersion(version, stillCurrent);
            // Another chart may have added or evicted days while this range was loading.
            var days = new Dictionary<DateOnly, BudgetOperationDto[]>(basis.Days);
            foreach (var day in cached.Days)
                days[day.Key] = day.Value;
            foreach (var day in loaded)
                if (!cached.Days.ContainsKey(day.Key))
                    days[day.Key] = day.Value;
            var openings = new Dictionary<DateOnly, BudgetOperationDto[]>(basis.Openings);
            foreach (var anchor in cached.Openings)
                openings[anchor.Key] = anchor.Value;
            if (opening is not null && openingRange is { } range && !cached.Openings.ContainsKey(first)
                && Between(range.First, range.Last).All(day =>
                    !cached.Days.TryGetValue(day, out var confirmed) || confirmed.SequenceEqual(loaded[day])))
                openings[first] = opening;
            var merged = cached with { Days = days, Openings = openings };
            if (cached.DirtyFrom is { } from && cached.DirtyThrough is { } through)
            {
                using var pending = GetMissingRanges(days, from, through).GetEnumerator();
                merged = pending.MoveNext()
                    ? merged with { DirtyFrom = pending.Current.First }
                    : merged with { DirtyFrom = null, DirtyThrough = null };
            }
            var operations = Collect(merged, first, last, includeOpening, opening, openingRange, loaded);
            Trim(merged, first, last);
            await WriteAsync(merged, version, stillCurrent, token).ConfigureAwait(false);
            CheckVersion(version, stillCurrent);
            state = merged;
            return operations;
        }
        finally
        {
            gate.Release();
        }

        async Task<BudgetOperationDto[]> LoadUncachedAsync()
        {
            var operations = await reads.Start(new Range(first, last), includeOpening).WaitAsync(token).ConfigureAwait(false);
            CheckVersion(version, stillCurrent);
            return operations;
        }
    }

    internal async Task InvalidateAsync(string identity, DateOnly? firstAffected)
    {
        var (version, turn) = BeginInvalidation();
        await turn.ConfigureAwait(false);
        try
        {
            var cached = await ReadAsync(identity, CancellationToken.None).ConfigureAwait(false);
            if (firstAffected is { } day)
            {
                var last = cached.Days.Keys.Concat(cached.DirtyThrough is { } through ? [through] : [])
                    .DefaultIfEmpty(DateOnly.MinValue).Max();
                if (day <= last && cached.Days.Keys.Any(date => date < day))
                {
                    var dirty = cached.DirtyFrom is { } previous && previous < day ? previous : day;
                    cached = cached with { DirtyFrom = dirty, DirtyThrough = last };
                }
                foreach (var date in cached.Days.Keys.Where(date => date >= day).ToArray())
                    cached.Days.Remove(date);
                foreach (var date in cached.Openings.Keys.Where(date => date > day).ToArray())
                    cached.Openings.Remove(date);
            }
            else
            {
                cached.Days.Clear();
                cached.Openings.Clear();
                cached = cached with { DirtyFrom = null, DirtyThrough = null };
            }
            state = cached;

            // Invalidation remains durable even when several edits arrive before the next chart request.
            if (!await WriteAsync(cached, version, static () => true, CancellationToken.None).ConfigureAwait(false)
                && IsCurrent(version))
                RemoveFile();
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task ClearAsync()
    {
        var (_, turn) = BeginInvalidation();
        await turn.ConfigureAwait(false);
        try
        {
            state = null;
            RemoveFile();
        }
        finally
        {
            gate.Release();
        }
    }

    private (long Version, Task Turn) BeginInvalidation()
    {
        lock (revisions)
            return (Interlocked.Increment(ref generation), gate.WaitAsync());
    }

    private LoadFlight AcquireLoad(LoadKey key, Func<DateOnly, DateOnly, bool, CancellationToken, Task<BudgetOperationDto[]>> load)
    {
        LoadFlight? flight;
        var start = false;
        lock (revisions)
        {
            if (!IsCurrent(key.Generation))
                throw new StatisticsCacheInvalidatedException();
            if (!loads.TryGetValue(key, out flight))
            {
                flight = new LoadFlight();
                loads.Add(key, flight);
                start = true;
            }
            flight.Readers++;
        }
        if (start)
            _ = RunLoadAsync(key, flight, load);
        return flight;
    }

    private static async Task RunLoadAsync(LoadKey key, LoadFlight flight,
        Func<DateOnly, DateOnly, bool, CancellationToken, Task<BudgetOperationDto[]>> load)
    {
        using var cancellation = flight.Cancellation;
        try
        {
            var rows = await load(key.Range.First, key.Range.Last, key.IncludeOpening, cancellation.Token).ConfigureAwait(false);
            flight.Source.TrySetResult(rows);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            flight.Source.TrySetCanceled(cancellation.Token);
        }
        catch (Exception error)
        {
            flight.Source.TrySetException(error);
            // A previous request can fail before a consumer reaches this task.
            _ = flight.Source.Task.Exception;
        }
    }

    private void ReleaseLoad(LoadKey key, LoadFlight flight)
    {
        lock (revisions)
        {
            if (--flight.Readers != 0)
                return;
            loads.Remove(key);
        }
        if (flight.Source.Task.IsCompleted)
            return;
        try { flight.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private bool IsCurrent(long version) => version == Interlocked.Read(ref generation);

    private void CheckVersion(long version, Func<bool> stillCurrent)
    {
        if (!IsCurrent(version) || !stillCurrent())
            throw new StatisticsCacheInvalidatedException();
    }

    private async Task<CacheState> ReadAsync(string identity, CancellationToken token)
    {
        if (state?.Identity == identity)
            return state;
        if (path is not null)
        {
            try
            {
                var restored = await Task.Run(() => ReadFile(path), token).ConfigureAwait(false);
                if (restored is { Version: FormatVersion, Days: not null, Openings: not null } && restored.Identity == identity
                    && restored.Days.Count <= MaxDays && restored.Openings.Count <= MaxOpenings)
                    return state = restored;
            }
            catch (Exception error) when (IsFileError(error) || error is JsonException)
            {
                // A lost or unreadable cache is equivalent to an empty cache.
            }
        }
        return state = new CacheState(FormatVersion, identity, [], []);
    }

    private static CacheState? ReadFile(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length > MaxFileBytes)
            return null;
        var data = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<CacheState>(data);
    }

    private async Task<bool> WriteAsync(CacheState cached, long version, Func<bool> stillCurrent, CancellationToken token)
    {
        if (path is null)
            return true;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var data = await Task.Run(() => ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(cached),
                null, DataProtectionScope.CurrentUser), token).ConfigureAwait(false);
            if (data.Length > MaxFileBytes)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(temp, data, token).ConfigureAwait(false);
            lock (revisions)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrent(version) || !stillCurrent())
                    return false;
                File.Move(temp, path, overwrite: true);
            }
            return true;
        }
        catch (Exception error) when (IsFileError(error))
        {
            return false;
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception error) when (IsFileError(error)) { }
        }
    }

    private void RemoveFile()
    {
        if (path is null)
            return;
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }
    }

    private static bool IsFileError(Exception error) => error is IOException
        or UnauthorizedAccessException or CryptographicException or System.Security.SecurityException;

    private static IEnumerable<Range> GetMissingRanges(Dictionary<DateOnly, BudgetOperationDto[]> days,
        DateOnly first, DateOnly last)
    {
        DateOnly? current = null;
        foreach (var day in Between(first, last))
        {
            if (!days.ContainsKey(day))
                current ??= day;
            else if (current is { } start)
            {
                yield return new Range(start, day.AddDays(-1));
                current = null;
            }
        }
        if (current is { } remaining)
            yield return new Range(remaining, last);
    }

    private static DateOnly? FindOpening(CacheState cached, DateOnly first)
    {
        foreach (var anchor in cached.Openings.Keys.Where(day => day <= first).OrderDescending())
        {
            if (anchor == first || !GetMissingRanges(cached.Days, anchor, first.AddDays(-1)).Any())
                return anchor;
        }
        return null;
    }

    private static BudgetOperationDto[] Collect(CacheState cached, DateOnly first, DateOnly last, bool includeOpening,
        BudgetOperationDto[]? fetchedOpening = null, Range? openingRange = null,
        Dictionary<DateOnly, BudgetOperationDto[]>? fetchedDays = null)
    {
        var result = new List<BudgetOperationDto>();
        if (fetchedOpening is not null)
            result.AddRange(fetchedOpening);
        else if (includeOpening && FindOpening(cached, first) is { } anchor)
        {
            result.AddRange(cached.Openings[anchor]);
            if (anchor != first)
            {
                foreach (var day in Between(anchor, first.AddDays(-1)))
                    result.AddRange(cached.Days[day].Select(row => row with
                    {
                        IsOpening = true, OpeningCount = 1, RateDate = row.RateDate ?? day
                    }));
            }
        }
        foreach (var day in Between(first, last))
            result.AddRange(openingRange is { } range && day >= range.First && day <= range.Last
                ? fetchedDays![day]
                : cached.Days[day]);
        return result.ToArray();
    }

    private static bool AddDays(Dictionary<DateOnly, BudgetOperationDto[]> days, Range range,
        BudgetOperationDto[] operations, Func<BudgetOperationDto, DateOnly?> reportDay)
    {
        var dated = new Dictionary<DateOnly, List<BudgetOperationDto>>();
        foreach (var row in operations)
        {
            if (row.IsOpening)
                continue;
            if (reportDay(row) is not { } day || day < range.First || day > range.Last)
                return false; // Preserve ambiguous legacy rows via the established uncached path.
            if (!dated.TryGetValue(day, out var group))
                dated[day] = group = [];
            group.Add(row);
        }
        foreach (var day in Between(range.First, range.Last))
            days[day] = dated.TryGetValue(day, out var group) ? group.ToArray() : [];
        return true;
    }

    private static void Trim(CacheState cached, DateOnly first, DateOnly last)
    {
        static int Distance(DateOnly date, DateOnly first, DateOnly last) =>
            date < first ? first.DayNumber - date.DayNumber : Math.Max(0, date.DayNumber - last.DayNumber);
        foreach (var anchor in cached.Openings.Keys.OrderBy(day => Distance(day, first, last)).Skip(MaxOpenings).ToArray())
            cached.Openings.Remove(anchor);
        var rowCount = cached.Days.Values.Sum(rows => (long)rows.Length) + cached.Openings.Values.Sum(rows => (long)rows.Length);
        while (rowCount > MaxRows && cached.Openings.Count > 0)
        {
            var anchor = cached.Openings.Keys.MaxBy(day => Distance(day, first, last));
            rowCount -= cached.Openings[anchor].Length;
            cached.Openings.Remove(anchor);
        }
        foreach (var day in cached.Days.Keys.OrderByDescending(day => Distance(day, first, last)).ToArray())
        {
            if (cached.Days.Count <= MaxDays && rowCount <= MaxRows)
                break;
            rowCount -= cached.Days[day].Length;
            cached.Days.Remove(day);
        }
    }

    private static IEnumerable<DateOnly> Between(DateOnly first, DateOnly last)
    {
        for (var day = first.DayNumber; day <= last.DayNumber; day++)
            yield return DateOnly.FromDayNumber(day);
    }

    private readonly record struct Range(DateOnly First, DateOnly Last);
    private readonly record struct LoadKey(string Identity, long Generation, Range Range, bool IncludeOpening);

    private sealed class LoadFlight
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<BudgetOperationDto[]> Source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Readers;
    }

    private sealed class LoadReads(BudgetDayCache owner, string identity, long generation,
        Func<DateOnly, DateOnly, bool, CancellationToken, Task<BudgetOperationDto[]>> load, CancellationToken token)
        : IDisposable
    {
        private readonly Dictionary<LoadKey, LoadFlight> flights = new();

        internal Task<BudgetOperationDto[]> Start(Range range, bool includeOpening)
        {
            token.ThrowIfCancellationRequested();
            var key = new LoadKey(identity, generation, range, includeOpening);
            if (!flights.TryGetValue(key, out var flight))
            {
                flight = owner.AcquireLoad(key, load);
                flights.Add(key, flight);
            }
            return flight.Source.Task;
        }

        public void Dispose()
        {
            foreach (var read in flights)
                owner.ReleaseLoad(read.Key, read.Value);
        }
    }

    private sealed record CacheState(int Version, string Identity, Dictionary<DateOnly, BudgetOperationDto[]> Days,
        Dictionary<DateOnly, BudgetOperationDto[]> Openings, DateOnly? DirtyFrom = null, DateOnly? DirtyThrough = null);
}
