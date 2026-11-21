using Castar.Compontents;
using Castar.LocalAi;
using Castar.ServerConnection;
using Castar.Settings;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Contents;

public partial class LocalAiSettingsContent : UserControl
{
    private const string LlamaDefaultEndpoint = "http://127.0.0.1:8080/";
    private readonly ClientSettings settings;
    private readonly LocalAiService service;
    private readonly ExpenseCategorizationService? categorization;
    private CancellationTokenSource? discovery;
    private LocalAiOptions saved = LocalAiOptions.Default;
    private bool savedEnabled;
    private bool editingDraft;
    private bool changingControls = true;
    private bool startingHistory;
    private ExpenseHistoryProgress? historyBeforeStart;
    private string? historyError;
    private LocalAiProvider lastProvider;
    private (LocalAiProvider Provider, LocalAiMode Mode, string? Endpoint, string? Executable, string? Directory) currentTarget;

    internal LocalAiSettingsContent(ClientSettings settings, LocalAiService service, ExpenseCategorizationService? categorization = null)
    {
        this.settings = settings;
        this.service = service;
        this.categorization = categorization;
        InitializeComponent();
        HistoryPanel.Visibility = categorization is null ? Visibility.Collapsed : Visibility.Visible;
        ProviderPicker.ItemsSource = new Choice<LocalAiProvider>[]
        {
            new(LocalAiProvider.Ollama, "Ollama"),
            new(LocalAiProvider.LlamaCpp, "llama.cpp")
        };
        ModePicker.ItemsSource = new Choice<LocalAiMode>[]
        {
            new(LocalAiMode.External, "Внешний сервер"),
            new(LocalAiMode.Managed, "Управляется Castar")
        };
        IdlePicker.ItemsSource = new Choice<int>[]
        {
            new(0, "Сразу"),
            new(5, "5 минут"),
            new(15, "15 минут"),
            new(-1, "Не выгружать")
        };
        changingControls = false;
    }

    private void LocalAiSettings_Loaded(object sender, RoutedEventArgs e)
    {
        service.ProcessStateChanged += Service_ProcessStateChanged;
        if (categorization is not null)
            categorization.HistoryProgressChanged += Categorization_HistoryProgressChanged;
        if (IsVisible)
            BeginEditing();
    }

    private void LocalAiSettings_Unloaded(object sender, RoutedEventArgs e)
    {
        service.ProcessStateChanged -= Service_ProcessStateChanged;
        if (categorization is not null)
            categorization.HistoryProgressChanged -= Categorization_HistoryProgressChanged;
        EndEditing();
    }

    private void LocalAiSettings_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && IsLoaded)
            BeginEditing();
        else if (e.NewValue is false)
            EndEditing();
    }

    private void BeginEditing()
    {
        if (editingDraft)
            return;

        editingDraft = true;
        changingControls = true;
        try
        {
            saved = settings.LocalAi;
            savedEnabled = settings.AutomaticCategorizationEnabled;
            EnabledSwitch.IsChecked = savedEnabled;
            ProviderPicker.SelectedValue = lastProvider = saved.Provider;
            ModePicker.SelectedValue = saved.Mode;
            IdlePicker.SelectedValue = saved.IdleMinutes;
            EndpointInput.Text = saved.Endpoint;
            ExecutableInput.Text = saved.ExecutablePath ?? "";
            DirectoryInput.Text = saved.ModelDirectory ?? "";
            RestoreModel(saved.Model);
            currentTarget = ModelSource(saved);
            DiscoveryStatus.Text = "";
            ApplyStatus.Text = "";
        }
        finally
        {
            changingControls = false;
        }
        UpdateActions();
    }

    private void EndEditing()
    {
        editingDraft = false;
        discovery?.Cancel();
    }

    private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var selector = new OpenFileDialog
        {
            Title = "Исполняемый файл",
            Filter = "Программы (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (selector.ShowDialog(Window.GetWindow(this)) == true)
            ExecutableInput.Text = selector.FileName;
    }

    private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
    {
        var selector = new OpenFolderDialog { Title = "Папка моделей GGUF" };
        if (selector.ShowDialog(Window.GetWindow(this)) == true)
            DirectoryInput.Text = selector.FolderName;
    }

    private Task DiscoverAsync(object sender, LocalAiOptions draft) => ButtonLoading.RunAsync(sender, async () =>
    {
        using var request = new CancellationTokenSource();
        discovery = request;
        DiscoveryStatus.Text = "";
        UpdateProcessState();
        try
        {
            var found = await service.DiscoverModelsAsync(draft, request.Token);
            if (request.IsCancellationRequested || !editingDraft)
                return;

            var models = found.Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (models.Length == 0)
            {
                DiscoveryStatus.Text = "Модели не найдены.";
                return;
            }

            ModelPicker.ItemsSource = models;
            ModelPicker.SelectedItem = models.Contains(draft.Model, StringComparer.Ordinal)
                ? draft.Model : models.Length == 1 ? models[0] : null;
            UpdateActions();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception error) when (IsLocalServiceError(error))
        {
            if (!request.IsCancellationRequested && editingDraft)
                DiscoveryStatus.Text = DescribeError(error, "Не удалось получить список моделей.");
        }
        finally
        {
            discovery = null;
            UpdateProcessState();
        }
    });

    private async void DiscoverModels_Click(object sender, RoutedEventArgs e)
    {
        if (!editingDraft || discovery is not null)
            return;

        var draft = ReadDraft();
        if (ValidateTarget(draft) is { } error)
        {
            DiscoveryStatus.Text = error;
            return;
        }
        await DiscoverAsync(sender, draft);
    }

    private async void StartProcess_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartProcess())
            return;

        var options = settings.LocalAi;
        if (ValidateTarget(options) is { } error)
        {
            DiscoveryStatus.Text = error;
            return;
        }
        if (options.Model.Length == 0)
        {
            DiscoveryStatus.Text = "Выберите модель.";
            return;
        }

        await ButtonLoading.RunAsync(sender, async () =>
        {
            DiscoveryStatus.Text = "";
            try
            {
                await service.StartAsync(options);
            }
            catch (Exception startError) when (IsLocalServiceError(startError))
            {
                DiscoveryStatus.Text = DescribeError(startError, "Не удалось запустить процесс.");
            }
            finally
            {
                UpdateProcessState();
            }
        });
    }

    private async void StopProcess_Click(object sender, RoutedEventArgs e)
    {
        await ButtonLoading.RunAsync(sender, async () =>
        {
            if (!service.HasOwnedProcess)
                return;

            DiscoveryStatus.Text = "";
            discovery?.Cancel();
            try
            {
                await service.StopAsync();
            }
            catch (Exception error) when (IsLocalServiceError(error))
            {
                DiscoveryStatus.Text = DescribeError(error, "Не удалось остановить процесс.");
            }
            finally
            {
                UpdateProcessState();
            }
        });
    }

    private void Service_ProcessStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            UpdateProcessState();
        else if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.InvokeAsync(UpdateProcessState);
    }

    private void UpdateProcessState()
    {
        var ownsProcess = service.HasOwnedProcess;
        var managed = ModePicker.SelectedValue is LocalAiMode.Managed;
        ManagedProcessRow.Visibility = managed || ownsProcess ? Visibility.Visible : Visibility.Collapsed;
        ManagedProcessStatus.Text = ownsProcess ? "Управляемый процесс запущен" : "Управляемый процесс остановлен";
        StartProcessButton.Visibility = managed ? Visibility.Visible : Visibility.Collapsed;
        StartProcessButton.IsEnabled = CanStartProcess();
        StopProcessButton.IsEnabled = ownsProcess;
    }

    private bool CanStartProcess() => editingDraft && discovery is null && !service.HasOwnedProcess
        && settings.LocalAi.Mode == LocalAiMode.Managed && settings.LocalAi == ReadDraft()
        && settings.AutomaticCategorizationEnabled == (EnabledSwitch.IsChecked == true);

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var draft = ReadDraft();
        var enable = EnabledSwitch.IsChecked == true;
        if (enable && (ValidateTarget(draft) ?? (draft.Model.Length == 0 ? "Выберите модель." : null)) is { } error)
        {
            ApplyStatus.Text = error;
            return;
        }

        settings.LocalAi = saved = draft;
        settings.AutomaticCategorizationEnabled = savedEnabled = enable;
        ApplyStatus.Text = "";
        UpdateActions();
    }

    private async void ProcessHistory_Click(object sender, RoutedEventArgs e)
    {
        if (categorization is null || !CanProcessHistory(categorization.HistoryProgress))
            return;

        startingHistory = true;
        historyBeforeStart = categorization.HistoryProgress;
        historyError = null;
        UpdateHistoryProgress();
        try
        {
            await categorization.CategorizeHistoryAsync();
        }
        catch (Exception error) when (IsLocalServiceError(error))
        {
            historyError = DescribeError(error, "Не удалось обработать историю.");
        }
        finally
        {
            startingHistory = false;
            historyBeforeStart = null;
            UpdateHistoryProgress();
        }
    }

    private void Categorization_HistoryProgressChanged(ExpenseHistoryProgress progress)
    {
        if (Dispatcher.CheckAccess())
            UpdateVisibleHistoryProgress();
        else if (!Dispatcher.HasShutdownStarted)
            _ = Dispatcher.InvokeAsync(UpdateVisibleHistoryProgress);
    }

    private void UpdateVisibleHistoryProgress()
    {
        if (IsLoaded && IsVisible)
            UpdateHistoryProgress();
    }

    private void UpdateHistoryProgress()
    {
        if (categorization is null)
            return;

        var activeProgress = categorization.HistoryProgress;
        var progress = activeProgress.Kind is null ? activeProgress : new ExpenseHistoryProgress(0, 0, null, false, null);
        var running = startingHistory || progress.Running;
        var awaitingProgress = startingHistory && Equals(progress, historyBeforeStart);
        var hasTotal = !awaitingProgress && progress.Total > 0;
        ProcessHistoryButton.IsEnabled = CanProcessHistory(activeProgress);
        HistoryProgressBody.Visibility = running || hasTotal ? Visibility.Visible : Visibility.Collapsed;
        HistoryProgressLabels.Visibility = hasTotal ? Visibility.Visible : Visibility.Collapsed;
        HistoryCountText.Text = hasTotal ? $"Обработано {progress.Completed:N0} из {progress.Total:N0}" : "";
        HistoryRemainingText.Text = progress.Running && progress.Remaining is { } remaining
            ? $"Осталось ~{FormatRemaining(remaining)}" : "";
        HistoryProgressBar.IsIndeterminate = running && !hasTotal;
        HistoryProgressBar.Maximum = hasTotal ? progress.Total : 1;
        HistoryProgressBar.Value = hasTotal ? Math.Clamp(progress.Completed, 0, progress.Total) : 0;
        HistoryStatus.Text = historyError ?? (awaitingProgress ? null : progress.Message)
            ?? (running && !hasTotal ? "Подготовка истории…" : "");
    }

    private bool CanProcessHistory(ExpenseHistoryProgress progress) => !startingHistory && !progress.Running
        && settings.AutomaticCategorizationEnabled && !string.IsNullOrWhiteSpace(settings.LocalAi.Model);

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.FromMinutes(1))
            return $"{Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):N0} с";
        if (remaining < TimeSpan.FromHours(1))
            return $"{Math.Ceiling(remaining.TotalMinutes):N0} мин";
        return $"{Math.Ceiling(remaining.TotalHours):N0} ч";
    }

    private void TargetSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateTarget();

    private void TargetText_Changed(object sender, TextChangedEventArgs e) => UpdateTarget();

    private void UpdateTarget()
    {
        if (changingControls || !editingDraft)
            return;

        changingControls = true;
        try
        {
            var draft = ReadDraft();
            if (draft.Provider != lastProvider)
            {
                ExecutableInput.Text = draft.Provider == saved.Provider ? saved.ExecutablePath ?? "" : "";
                if (draft.Endpoint == LocalAiOptions.Default.Endpoint || draft.Endpoint == LlamaDefaultEndpoint)
                    EndpointInput.Text = draft.Provider == LocalAiProvider.LlamaCpp
                        ? LlamaDefaultEndpoint : LocalAiOptions.Default.Endpoint;
                draft = ReadDraft();
            }
            lastProvider = draft.Provider;
            if (ModelSource(draft) != currentTarget)
            {
                currentTarget = ModelSource(draft);
                discovery?.Cancel();
                DiscoveryStatus.Text = "";
                RestoreModel(currentTarget == ModelSource(saved) ? saved.Model : "");
            }
        }
        finally
        {
            changingControls = false;
        }
        UpdateDraft();
    }

    private void Draft_Changed(object sender, RoutedEventArgs e) => UpdateDraft();

    private void DraftSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateDraft();

    private void UpdateDraft()
    {
        if (changingControls || !editingDraft)
            return;

        ApplyStatus.Text = "";
        UpdateActions();
    }

    private void UpdateActions()
    {
        ApplyButton.IsEnabled = ReadDraft() != saved || (EnabledSwitch.IsChecked == true) != savedEnabled;
        UpdateProcessState();
        UpdateHistoryProgress();
    }

    private void RestoreModel(string model)
    {
        ModelPicker.ItemsSource = model.Length == 0 ? Array.Empty<string>() : new[] { model };
        ModelPicker.SelectedItem = model.Length == 0 ? null : model;
    }

    private LocalAiOptions ReadDraft()
    {
        var provider = ProviderPicker.SelectedValue is LocalAiProvider selected ? selected : saved.Provider;
        return new(provider,
            ModePicker.SelectedValue is LocalAiMode mode ? mode : saved.Mode,
            EndpointInput.Text.Trim(),
            ModelPicker.SelectedItem as string ?? "",
            TrimToNull(ExecutableInput.Text),
            provider == LocalAiProvider.LlamaCpp ? TrimToNull(DirectoryInput.Text) : null,
            IdlePicker.SelectedValue is int idle ? idle : saved.IdleMinutes);
    }

    private static string? TrimToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (LocalAiProvider Provider, LocalAiMode Mode, string? Endpoint, string? Executable, string? Directory)
        ModelSource(LocalAiOptions options) => options.Mode == LocalAiMode.External
        ? (options.Provider, options.Mode, options.Endpoint, null, null)
        : (options.Provider, options.Mode, null, options.ExecutablePath,
            options.Provider == LocalAiProvider.LlamaCpp ? options.ModelDirectory : null);

    private static string? ValidateTarget(LocalAiOptions options)
    {
        if (options.Mode == LocalAiMode.External)
        {
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                return "Укажите HTTP- или HTTPS-адрес API.";
            return null;
        }

        return options.Provider == LocalAiProvider.LlamaCpp && string.IsNullOrEmpty(options.ModelDirectory)
            ? "Выберите папку моделей GGUF." : null;
    }

    private static bool IsLocalServiceError(Exception error) => error is ArgumentException or InvalidOperationException
        or IOException or HttpRequestException or UnauthorizedAccessException or Win32Exception
        or InvalidDataException or OperationCanceledException or TimeoutException or JsonException;

    private static string DescribeError(Exception error, string fallback) => error switch
    {
        HttpRequestException { StatusCode: { } status } => $"{fallback.TrimEnd('.')} (HTTP {(int)status}).",
        HttpRequestException => fallback,
        OperationCanceledException or TimeoutException => "Истекло время ожидания ответа локального сервиса.",
        JsonException => "Ответ локального сервиса имеет неверный формат.",
        _ => error.Message
    };

    private sealed record Choice<T>(T Value, string Label);
}
