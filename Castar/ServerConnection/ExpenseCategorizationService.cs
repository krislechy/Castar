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

public sealed record ExpenseHistoryProgress(int Completed, int Total, TimeSpan? Remaining, bool Running, string? Message)
{
    public string? Kind { get; init; }
    public DateTime? StartUtc { get; init; }
    public DateTime? EndUtc { get; init; }
}

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
    private ExplicitRun? historyRequest;
    private ExpenseHistoryProgress historyProgress = new(0, 0, null, false, null);
    private bool pending;
    private bool shouldStopModel;
    private bool started;
    private bool disposed;

    public event Action<ExpenseHistoryProgress>? HistoryProgressChanged;
    public ExpenseHistoryProgress HistoryProgress => Volatile.Read(ref historyProgress);

    public Task CategorizeHistoryAsync(CancellationToken token = default) => StartRun(null, null, null, token);

    public Task CategorizePeriodAsync(string kind, DateTime startUtc, DateTime endUtc, CancellationToken token = default)
    {
        if (kind is not (BudgetEvents.Purchase or BudgetEvents.Income))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (endUtc <= startUtc)
            throw new ArgumentException("Укажите допустимый период.", nameof(endUtc));
        return StartRun(kind, startUtc, endUtc, token);
    }

    private Task StartRun(string? kind, DateTime? startUtc, DateTime? endUtc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task completion;
        lock (gate)
        {
            if (historyRequest is { } existing)
            {
                if (existing.Kind == kind && existing.StartUtc == startUtc && existing.EndUtc == endUtc)
                    return existing.Completion.Task.WaitAsync(token);
                throw new InvalidOperationException("Дождитесь завершения текущей категоризации.");
            }
            if (disposed || !started)
                throw new InvalidOperationException("История доступна в активном клиенте категоризации.");
            if ((kind is null && !settings.AutomaticCategorizationEnabled)
                || settings.LocalAi.Model.Length == 0 || settings.ApiKey.Length == 0)
                throw new InvalidOperationException("Подключите сервер и примените настройки модели.");

            historyRequest = new(kind, startUtc, endUtc);
            completion = historyRequest.Completion.Task;
            Volatile.Write(ref historyProgress, historyRequest.Scope(new(0, 0, null, true, null)));
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
        if (change.Kind is BudgetEvents.Purchase or BudgetEvents.Income)
            Schedule();
    }

    private void ConfigurationChanged(ServerSynchronizationDto change)
    {
        if (change.Kind == ServerSynchronization.Categories)
            Schedule();
        else if (change.Kind is ServerSynchronization.PurchaseClassificationRequested or ServerSynchronization.IncomeClassificationRequested)
            // An explicit run already owns its queue.
            Request(stopModel: false, onlyWhenNoExplicitRun: true);
    }

    private void ConnectionChanged(object? sender, EventArgs e) => Request(stopModel: true);

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClientSettings.AutomaticCategorizationEnabled) or nameof(ClientSettings.LocalAi))
            Request(stopModel: true);
    }

    private void Schedule() => Request(stopModel: false);

    private void Request(bool stopModel, bool onlyWhenNoExplicitRun = false)
    {
        lock (gate)
        {
            if (disposed || (onlyWhenNoExplicitRun && historyRequest is not null)
                || (!stopModel && !settings.AutomaticCategorizationEnabled))
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
            ExplicitRun? history;
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
                if ((enabled || history?.Kind is not null) && settings.ApiKey.Length > 0)
                {
                    if (history is not null)
                    {
                        var kind = history.Kind ?? BudgetEvents.Purchase;
                        var queued = await server.QueueClassificationAsync(kind, request.Token,
                            history.StartUtc, history.EndUtc).ConfigureAwait(false);
                        var tally = new HistoryTally(queued.PendingCount, progress => ReportHistory(history.Scope(progress)));
                        message = kind == BudgetEvents.Income
                            ? await ClassifyPendingIncomesAsync(options, tally, request.Token, history).ConfigureAwait(false)
                            : await ClassifyPendingAsync(options, tally, request.Token, history).ConfigureAwait(false);
                    }
                    else
                    {
                        await ClassifyPendingAsync(options, null, request.Token).ConfigureAwait(false);
                        try
                        {
                            await ClassifyPendingIncomesAsync(options, null, request.Token).ConfigureAwait(false);
                        }
                        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
                        {
                            // Older servers support purchase categorization only.
                        }
                    }
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
                    history!.Completion.TrySetResult();
                }
            }
        }
    }

    private async Task<string?> ClassifyPendingAsync(LocalAiOptions options, HistoryTally? history, CancellationToken token,
        ExplicitRun? scope = null)
    {
        LocalAiCategory[]? categories = null;
        var conflictRetries = 0;
        while (true)
        {
            var (work, remaining) = await server.GetPendingPurchaseClassificationAsync(history is not null, token,
                scope?.StartUtc, scope?.EndUtc).ConfigureAwait(false);
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
                        categories ??= await ReadCategoriesAsync(BudgetEvents.Purchase, token).ConfigureAwait(false);
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
                        unavailable = error is LocalAiProtocol.ContextLimitException
                            ? error.Message : "Локальная модель недоступна. Необработанные товары остались в очереди.";
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

    private async Task<string?> ClassifyPendingIncomesAsync(LocalAiOptions options, HistoryTally? history,
        CancellationToken token, ExplicitRun? scope = null)
    {
        LocalAiCategory[]? categories = null;
        var conflictRetries = 0;
        while (true)
        {
            var (work, remaining) = await server.GetPendingIncomeClassificationAsync(history is not null, token,
                scope?.StartUtc, scope?.EndUtc).ConfigureAwait(false);
            if (remaining.HasValue)
                history?.Reconcile(remaining.Value);
            if (work.Length == 0)
                return history is { Total: 0 } ? "Подходящих доходов нет." : null;

            var assignments = work.Where(row => row.HasHistory).ToDictionary(row => row.IncomeId, row => row.CategoryId);
            var items = work.Where(row => !row.HasHistory).Select(row => new LocalAiItem(row.IncomeId, row.Source)).ToArray();
            string? unavailable = null;
            if (items.Length > 0)
            {
                try
                {
                    categories ??= await ReadCategoriesAsync(BudgetEvents.Income, token).ConfigureAwait(false);
                    if (categories.Length == 0)
                        unavailable = "Добавьте категории доходов.";
                    else
                    {
                        var proposed = await localAi.ClassifyAsync(options, null, items, categories, token, isIncome: true)
                            .ConfigureAwait(false);
                        foreach (var result in proposed)
                            assignments.Add(result.ItemId, result.CategoryId);
                    }
                }
                catch (Exception error) when (!token.IsCancellationRequested && IsRecoverable(error))
                {
                    logger.Log<ExpenseCategorizationService>("Локальная модель недоступна: " + error.GetType().Name, TypeLog.Warning);
                    unavailable = error is LocalAiProtocol.ContextLimitException
                        ? error.Message : "Локальная модель недоступна. Необработанные доходы остались в очереди.";
                }
            }

            var wrote = false;
            var conflicted = false;
            foreach (var row in work)
            {
                token.ThrowIfCancellationRequested();
                if (!assignments.TryGetValue(row.IncomeId, out var categoryId))
                    continue;
                try
                {
                    if (await server.ApplyIncomeClassificationAsync(row.IncomeId, row.Revision, categoryId, token).ConfigureAwait(false))
                    {
                        wrote = true;
                        history?.Applied(1);
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
                return "Часть доходов осталась в очереди. Повторите обработку.";
            if (wrote)
                conflictRetries = 0;
        }
    }

    private async Task<LocalAiCategory[]> ReadCategoriesAsync(string kind, CancellationToken token) =>
        (await server.GetBudgetCategoriesAsync(token).ConfigureAwait(false))
            .Where(category => category.Kind == kind)
            .Select(category => new LocalAiCategory(category.Id, category.Name, category.Tags)).ToArray();

    private void ReportHistory(ExpenseHistoryProgress progress)
    {
        Volatile.Write(ref historyProgress, progress);
        HistoryProgressChanged?.Invoke(progress);
    }

    private sealed record ExplicitRun(string? Kind, DateTime? StartUtc, DateTime? EndUtc)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExpenseHistoryProgress Scope(ExpenseHistoryProgress progress) => progress with
        {
            Kind = Kind,
            StartUtc = StartUtc,
            EndUtc = EndUtc
        };
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
            historyRequest?.Completion.TrySetCanceled();
        }
        settings.ConnectionChanged -= ConnectionChanged;
        settings.PropertyChanged -= SettingsChanged;
        events.Changed -= OperationChanged;
        events.ConfigurationChanged -= ConfigurationChanged;
        events.Synchronized -= Schedule;
    }
}
