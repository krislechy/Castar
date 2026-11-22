using Castar.Compontents;
using Castar.ServerConnection;
using Castar.Settings;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Contents;

internal sealed class PeriodClassification
{
    private readonly PeriodClassificationButton button;
    private readonly CastarServerClient server;
    private readonly ClientSettings settings;
    private readonly ExpenseCategorizationService? categorization;
    private readonly string kind;
    private readonly string target;
    private CancellationTokenSource? countRequest;
    private (DateTime StartUtc, DateTime EndUtc)? period;
    private bool hasCount;
    private bool submitting;

    internal PeriodClassification(PeriodClassificationButton button, CastarServerClient server, ClientSettings settings,
        ExpenseCategorizationService? categorization, string kind, string target)
    {
        this.button = button;
        this.server = server;
        this.settings = settings;
        this.categorization = categorization;
        this.kind = kind;
        this.target = target;
        button.Click += Classify_Click;
        button.Loaded += Attach;
        button.Unloaded += Detach;
        ToolTipService.SetShowOnDisabled(button, true);
        UpdateButton();
    }

    internal void Refresh(DateTime start, DateTime finish, bool force = true)
    {
        if (categorization is null)
            return;
        if (start > finish || settings.ApiKey.Length == 0)
        {
            Clear();
            return;
        }

        var bounds = server.GetUtcPeriod(start, finish);
        if (period != bounds)
        {
            Clear();
            period = bounds;
        }
        if (force || (!hasCount && countRequest is null))
            _ = RefreshCountAsync(bounds);
    }

    internal void Clear()
    {
        countRequest?.Cancel();
        countRequest = null;
        period = null;
        hasCount = false;
        button.Count = 0;
        UpdateButton();
    }

    private async Task RefreshCountAsync((DateTime StartUtc, DateTime EndUtc) bounds)
    {
        countRequest?.Cancel();
        using var request = new CancellationTokenSource();
        countRequest = request;
        try
        {
            // One request for a series of budget events from the same transaction or classification batch.
            await Task.Delay(150, request.Token);
            var count = await server.GetClassificationCountAsync(kind, bounds.StartUtc, bounds.EndUtc, request.Token);
            if (ReferenceEquals(countRequest, request) && !request.IsCancellationRequested)
            {
                hasCount = true;
                button.Count = count;
            }
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (ReferenceEquals(countRequest, request) && !request.IsCancellationRequested)
            {
                hasCount = false;
                button.Count = 0;
            }
        }
        finally
        {
            if (ReferenceEquals(countRequest, request))
            {
                countRequest = null;
                UpdateButton();
            }
        }
    }

    private async void Classify_Click(object sender, RoutedEventArgs e)
    {
        if (categorization is null || period is not { } bounds || !button.IsAvailable)
            return;

        submitting = true;
        UpdateButton();
        try
        {
            await categorization.CategorizePeriodAsync(kind, bounds.StartUtc, bounds.EndUtc);
            if (period == bounds)
            {
                var result = categorization.HistoryProgress;
                await RefreshCountAsync(bounds);
                if (period == bounds && button.IsVisible && result.Kind == kind
                    && result.StartUtc == bounds.StartUtc && result.EndUtc == bounds.EndUtc
                    && !string.IsNullOrEmpty(result.Message))
                    await PopupDialog.Show(result.Message, PopupDialog.TypeDialogEnum.Default);
            }
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error) || error is InvalidOperationException)
        {
            if (period == bounds && button.IsVisible)
                await PopupDialog.Show(CastarServerClient.DescribeError(error), PopupDialog.TypeDialogEnum.Default);
        }
        finally
        {
            submitting = false;
            UpdateButton();
        }
    }

    private void Attach(object sender, RoutedEventArgs e)
    {
        if (categorization is null)
            return;
        categorization.HistoryProgressChanged += ProgressChanged;
        settings.PropertyChanged += SettingsChanged;
        UpdateButton();
    }

    private void Detach(object sender, RoutedEventArgs e)
    {
        if (categorization is null)
            return;
        categorization.HistoryProgressChanged -= ProgressChanged;
        settings.PropertyChanged -= SettingsChanged;
        countRequest?.Cancel();
    }

    private void ProgressChanged(ExpenseHistoryProgress progress)
    {
        if (button.Dispatcher.CheckAccess())
            UpdateButton();
        else if (!button.Dispatcher.HasShutdownStarted)
            _ = button.Dispatcher.InvokeAsync(() => UpdateButton());
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClientSettings.AutomaticCategorizationEnabled) or nameof(ClientSettings.LocalAi))
            UpdateButton();
    }

    private void UpdateButton()
    {
        var modelConfigured = !string.IsNullOrWhiteSpace(settings.LocalAi.Model);
        var progress = categorization?.HistoryProgress;
        var currentProgress = progress is not null && progress.Kind == kind
            && progress.StartUtc == period?.StartUtc && progress.EndUtc == period?.EndUtc ? progress : null;
        button.IsBusy = submitting || currentProgress is { Running: true };
        if (button.IsBusy)
            button.ShowProgress(currentProgress?.Completed ?? 0, currentProgress?.Total ?? 0);
        else
            button.ProgressText = "";
        button.IsAvailable = categorization is not null && hasCount && button.Count > 0 && modelConfigured
            && !submitting && progress?.Running != true;
        button.ToolTip = button.IsBusy ? null
            : !modelConfigured ? "Выберите модель в настройках автоматической категоризации."
            : button.Count > 0 ? $"{target} без категории · {button.Count:N0}" : "Категоризация за выбранный период";
    }
}
