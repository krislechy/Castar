using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Castar.Api;
using Castar.LocalAi;
using Castar.Logger;
using Castar.Settings;

namespace Castar.ServerConnection;

public sealed record ExpenseHistoryProgress(int Completed, int Total, TimeSpan? Remaining, bool Running, string? Message);

public sealed class ExpenseCategorizationService(
    ClientSettings settings,
    CastarServerClient server,
    BudgetEventConnection events,
    LocalAiService localAi,
    ICLogger logger) : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? active;
    private Task? worker;
    private TaskCompletionSource? historyRequest;
    private ExpenseHistoryProgress historyProgress = new(0, 0, null, false, null);
    private bool pending;
    private bool shouldStopModel;
    private bool started;
    private bool disposed;

    public event Action<ExpenseHistoryProgress>? HistoryProgressChanged;
    public ExpenseHistoryProgress HistoryProgress => Volatile.Read(ref historyProgress);

    public Task CategorizeHistoryAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task completion;
        lock (gate)
        {
            if (historyRequest is not null)
                return historyRequest.Task.WaitAsync(token);
            if (disposed || !started)
                throw new InvalidOperationException("История доступна в активном клиенте категоризации.");
            if (!settings.AutomaticCategorizationEnabled || settings.LocalAi.Model.Length == 0 || settings.ApiKey.Length == 0)
                throw new InvalidOperationException("Подключите сервер и примените настройки модели.");

            historyRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = historyRequest.Task;
            Volatile.Write(ref historyProgress, new ExpenseHistoryProgress(0, 0, null, true, null));
            active?.Cancel();
            pending = true;
            worker ??= Task.Run(DrainAsync);
        }
        HistoryProgressChanged?.Invoke(HistoryProgress);
        return completion.WaitAsync(token);
    }

    public void Start()
    {
        if (started)
            return;

        started = true;
        settings.ConnectionChanged += ConnectionChanged;
        settings.PropertyChanged += SettingsChanged;
        events.Changed += OperationChanged;
        events.ConfigurationChanged += ConfigurationChanged;
        events.Synchronized += Schedule;
        Schedule();
    }

    private void OperationChanged(BudgetEventDto change)
    {
        if (change.Kind == BudgetEvents.Purchase)
            Schedule();
    }

    private void ConfigurationChanged(ServerSynchronizationDto change)
    {
        if (change.Kind is ServerSynchronization.Categories or ServerSynchronization.PurchaseClassificationRequested)
            Schedule();
    }

    private void ConnectionChanged(object? sender, EventArgs e) => Request(stopModel: true);

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClientSettings.AutomaticCategorizationEnabled) or nameof(ClientSettings.LocalAi))
            Request(stopModel: true);
    }

    private void Schedule() => Request(stopModel: false);

    private void Request(bool stopModel)
    {
        lock (gate)
        {
            if (disposed || (!stopModel && !settings.AutomaticCategorizationEnabled))
                return;

            if (stopModel)
            {
                shouldStopModel = true;
                active?.Cancel();
            }
            pending = true;
            worker ??= Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            using var request = new CancellationTokenSource();
            LocalAiOptions options;
            bool enabled;
            bool stopModel;
            TaskCompletionSource? history;
            lock (gate)
            {
                if (disposed || !pending)
                {
                    worker = null;
                    return;
                }
                active = request;
                pending = false;
                options = settings.LocalAi;
                enabled = settings.AutomaticCategorizationEnabled;
                stopModel = shouldStopModel;
                shouldStopModel = false;
                history = historyRequest;
            }

            string? message = null;
            try
            {
                if (stopModel)
                    await localAi.StopAsync(request.Token).ConfigureAwait(false);
                if (enabled && settings.ApiKey.Length > 0)
                {
                    HistoryTally? tally = null;
                    if (history is not null)
                    {
                        var queued = await server.QueuePurchaseClassificationAsync(request.Token).ConfigureAwait(false);
                        tally = new(queued.PendingCount, ReportHistory);
                    }
                    message = await ClassifyPendingAsync(options, tally, request.Token).ConfigureAwait(false);
                }
                else if (history is not null)
                    message = "Обработка остановлена.";
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                message = "Обработка остановлена.";
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                message = "Обновите Castar Server для обработки истории.";
            }
            catch (Exception error) when (IsRecoverable(error))
            {
                logger.Log<ExpenseCategorizationService>("Категоризация отложена: " + error.GetType().Name, TypeLog.Warning);
                message = CastarServerClient.DescribeError(error);
            }
            finally
            {
                ExpenseHistoryProgress? finished = null;
                lock (gate)
                {
                    active = null;
                    if (history is not null)
                    {
                        finished = HistoryProgress with { Running = false, Remaining = null, Message = message };
                        Volatile.Write(ref historyProgress, finished);
                        historyRequest = null;
                    }
                }
                if (finished is not null)
                {
                    HistoryProgressChanged?.Invoke(finished);
                    history!.TrySetResult();
                }
            }
        }
    }

    private async Task<string?> ClassifyPendingAsync(LocalAiOptions options, HistoryTally? history, CancellationToken token)
    {
        LocalAiCategory[]? categories = null;
        var conflictRetries = 0;
        while (true)
        {
            var (work, remaining) = await server.GetPendingPurchaseClassificationAsync(history is not null, token).ConfigureAwait(false);
            if (remaining.HasValue)
                history?.Reconcile(remaining.Value);
            if (work.Length == 0)
                return history is { Total: 0 } ? "Подходящих товаров нет." : null;

            var wrote = false;
            var conflicted = false;
            string? unavailable = null;
            foreach (var purchase in work)
            {
                token.ThrowIfCancellationRequested();
                var assignments = purchase.Items.Where(item => item.HasHistory)
                    .Select(item => new PurchaseClassificationResultDto(item.ItemId, item.CategoryId)).ToList();
                var unknown = purchase.Items.Where(item => !item.HasHistory).ToArray();
                if (unavailable is null && unknown.Length > 0)
                {
                    try
                    {
                        categories ??= (await server.GetBudgetCategoriesAsync(token).ConfigureAwait(false))
                            .Where(category => category.Kind == BudgetEvents.Purchase)
                            .Select(category => new LocalAiCategory(category.Id, category.Name)).ToArray();
                        if (categories.Length == 0)
                        {
                            unavailable = "Добавьте категории расходов.";
                        }
                        else
                        {
                            var items = unknown.Select(item => new LocalAiItem(item.ItemId, item.Name)).ToArray();
                            var proposed = await localAi.ClassifyAsync(options, purchase.Merchant, items, categories, token)
                                .ConfigureAwait(false);
                            assignments.AddRange(proposed.Select(result => new PurchaseClassificationResultDto(result.ItemId, result.CategoryId)));
                        }
                    }
                    catch (Exception error) when (!token.IsCancellationRequested && IsRecoverable(error))
                    {
                        logger.Log<ExpenseCategorizationService>("Локальная модель недоступна: " + error.GetType().Name, TypeLog.Warning);
                        unavailable = "Локальная модель недоступна. Необработанные товары остались в очереди.";
                    }
                }

                if (assignments.Count == 0)
                    continue;
                token.ThrowIfCancellationRequested();
                try
                {
                    if (await server.ApplyPurchaseClassificationAsync(purchase.PurchaseId, purchase.Revision, assignments, token)
                            .ConfigureAwait(false))
                    {
                        wrote = true;
                        history?.Applied(assignments.Count);
                    }
                }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict)
                {
                    conflicted = true;
                }
            }

            if (unavailable is not null)
                return unavailable;
            if (!wrote && (!conflicted || ++conflictRetries > 2))
                return "Часть товаров осталась в очереди. Повторите обработку.";
            if (wrote)
                conflictRetries = 0;
        }
    }

    private void ReportHistory(ExpenseHistoryProgress progress)
    {
        Volatile.Write(ref historyProgress, progress);
        HistoryProgressChanged?.Invoke(progress);
    }

    private sealed class HistoryTally
    {
        private readonly Stopwatch timer = Stopwatch.StartNew();
        private readonly Action<ExpenseHistoryProgress> report;
        private int completed;
        private int processedHere;
        public int Total { get; private set; }

        public HistoryTally(int total, Action<ExpenseHistoryProgress> report)
        {
            Total = total;
            this.report = report;
            Publish();
        }

        public void Reconcile(int remaining)
        {
            var expected = (long)completed + remaining;
            if (expected > Total)
                Total = (int)Math.Min(int.MaxValue, expected);
            else
                completed = Total - remaining;
            Publish();
        }

        public void Applied(int count)
        {
            processedHere = (int)Math.Min(int.MaxValue, (long)processedHere + count);
            completed = (int)Math.Min(Total, (long)completed + count);
            Publish();
        }

        private void Publish()
        {
            var left = Total - completed;
            var seconds = processedHere == 0 ? (double?)null : timer.Elapsed.TotalSeconds / processedHere * left;
            var estimate = seconds.HasValue && left > 0
                ? TimeSpan.FromSeconds(Math.Min(seconds.Value, TimeSpan.FromDays(365).TotalSeconds))
                : (TimeSpan?)null;
            report(new(completed, Total, estimate, true, null));
        }
    }

    private static bool IsRecoverable(Exception error) =>
        error is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException or OperationCanceledException or TimeoutException or Win32Exception or JsonException;

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            active?.Cancel();
            historyRequest?.TrySetCanceled();
        }
        settings.ConnectionChanged -= ConnectionChanged;
        settings.PropertyChanged -= SettingsChanged;
        events.Changed -= OperationChanged;
        events.ConfigurationChanged -= ConfigurationChanged;
        events.Synchronized -= Schedule;
    }
}
