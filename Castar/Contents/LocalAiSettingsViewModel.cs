using Castar.Domain.Models.SettingsModel;
using Castar.LocalAi;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Contents;

internal sealed class LocalAiSettingsViewModel(ClientSettings settings, LocalAiService service) : BaseSettingsModel
{
    private const string LlamaDefaultEndpoint = "http://127.0.0.1:8080/";
    private CancellationTokenSource? discovery;
    private LocalAiOptions saved = LocalAiOptions.Default;
    private LocalAiOptions draft = LocalAiOptions.Default;
    private bool savedEnabled;
    private bool enabled;
    private bool editing;
    private bool replacingModels;
    private string discoveryStatus = "";
    private string applyStatus = "";

    public IReadOnlyList<Choice<LocalAiProvider>> Providers { get; } =
    [
        new(LocalAiProvider.Ollama, "Ollama"),
        new(LocalAiProvider.LlamaCpp, "llama.cpp")
    ];

    public IReadOnlyList<Choice<LocalAiMode>> Modes { get; } =
    [
        new(LocalAiMode.External, "Внешний сервер"),
        new(LocalAiMode.Managed, "Управляется Castar")
    ];

    public IReadOnlyList<Choice<int>> IdleOptions { get; } =
    [
        new(0, "Сразу"),
        new(5, "5 минут"),
        new(15, "15 минут"),
        new(-1, "Не выгружать")
    ];

    public bool Enabled
    {
        get => enabled;
        set
        {
            if (SetField(ref enabled, value))
                DraftChanged();
        }
    }

    public LocalAiProvider Provider
    {
        get => draft.Provider;
        set
        {
            if (draft.Provider == value)
                return;

            var endpoint = draft.Endpoint.Trim();
            if (endpoint == LocalAiOptions.Default.Endpoint || endpoint == LlamaDefaultEndpoint)
                endpoint = value == LocalAiProvider.LlamaCpp ? LlamaDefaultEndpoint : LocalAiOptions.Default.Endpoint;
            else
                endpoint = draft.Endpoint;

            var before = ReadDraft();
            draft = draft with
            {
                Provider = value,
                ExecutablePath = value == saved.Provider ? saved.ExecutablePath : null,
                Endpoint = endpoint
            };
            OnPropertyChanged(nameof(Provider));
            OnPropertyChanged(nameof(ExecutablePath));
            OnPropertyChanged(nameof(Endpoint));
            TargetChanged(before);
        }
    }

    public LocalAiMode Mode
    {
        get => draft.Mode;
        set
        {
            if (draft.Mode != value)
                UpdateTarget(draft with { Mode = value }, nameof(Mode));
        }
    }

    public string Endpoint
    {
        get => draft.Endpoint;
        set
        {
            if (draft.Endpoint != value)
                UpdateTarget(draft with { Endpoint = value }, nameof(Endpoint));
        }
    }

    public string ExecutablePath
    {
        get => draft.ExecutablePath ?? "";
        set
        {
            if (ExecutablePath != value)
                UpdateTarget(draft with { ExecutablePath = value }, nameof(ExecutablePath));
        }
    }

    public string ModelDirectory
    {
        get => draft.ModelDirectory ?? "";
        set
        {
            if (ModelDirectory != value)
                UpdateTarget(draft with { ModelDirectory = value }, nameof(ModelDirectory));
        }
    }

    public int IdleMinutes
    {
        get => draft.IdleMinutes;
        set
        {
            if (draft.IdleMinutes == value)
                return;
            draft = draft with { IdleMinutes = value };
            OnPropertyChanged(nameof(IdleMinutes));
            DraftChanged();
        }
    }

    public string? SelectedModel
    {
        get => draft.Model.Length == 0 ? null : draft.Model;
        set
        {
            var model = value ?? "";
            if (replacingModels || model == draft.Model)
                return;
            draft = draft with { Model = model };
            OnPropertyChanged(nameof(SelectedModel));
            DraftChanged();
        }
    }

    public IReadOnlyList<string> Models { get; private set; } = [];

    public string DiscoveryStatus
    {
        get => discoveryStatus;
        private set => SetField(ref discoveryStatus, value);
    }

    public string ApplyStatus
    {
        get => applyStatus;
        private set => SetField(ref applyStatus, value);
    }

    public bool CanApply => editing && (ReadDraft() != saved || Enabled != savedEnabled);
    public bool CanDiscover => editing && discovery is null;
    public bool IsManaged => draft.Mode == LocalAiMode.Managed;
    public bool ShowProcess => IsManaged || CanStopProcess;
    public bool CanStartProcess => CanDiscover && !CanStopProcess
        && settings.LocalAi.Mode == LocalAiMode.Managed && settings.LocalAi == ReadDraft()
        && Enabled == settings.AutomaticCategorizationEnabled;
    public bool CanStopProcess => service.HasOwnedProcess;
    public string ProcessStatus => CanStopProcess ? "Управляемый процесс запущен" : "Управляемый процесс остановлен";

    internal void BeginEditing()
    {
        if (editing)
            return;

        editing = true;
        saved = draft = settings.LocalAi;
        enabled = savedEnabled = settings.AutomaticCategorizationEnabled;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IdleMinutes));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(ExecutablePath));
        OnPropertyChanged(nameof(ModelDirectory));
        RestoreModel(saved.Model);
        DiscoveryStatus = "";
        DraftChanged();
    }

    internal void EndEditing()
    {
        editing = false;
        discovery?.Cancel();
        NotifyActions();
    }

    internal async Task DiscoverAsync()
    {
        if (!CanDiscover)
            return;

        var options = ReadDraft();
        if (ValidateTarget(options) is { } error)
        {
            DiscoveryStatus = error;
            return;
        }

        using var request = new CancellationTokenSource();
        discovery = request;
        DiscoveryStatus = "";
        NotifyActions();
        try
        {
            var found = await service.DiscoverModelsAsync(options, request.Token);
            if (request.IsCancellationRequested || !editing)
                return;

            var models = found.Where(model => !string.IsNullOrWhiteSpace(model)).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (models.Length == 0)
            {
                DiscoveryStatus = "Модели не найдены.";
                return;
            }

            SetModels(models, models.Contains(options.Model, StringComparer.Ordinal)
                ? options.Model : models.Length == 1 ? models[0] : "");
            DraftChanged();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception discoveryError) when (IsLocalServiceError(discoveryError))
        {
            if (!request.IsCancellationRequested && editing)
                DiscoveryStatus = DescribeError(discoveryError, "Не удалось получить список моделей.");
        }
        finally
        {
            discovery = null;
            NotifyActions();
        }
    }

    internal async Task StartProcessAsync()
    {
        if (!CanStartProcess)
            return;

        var options = settings.LocalAi;
        if (ValidateConfiguration(options) is { } error)
        {
            DiscoveryStatus = error;
            return;
        }

        DiscoveryStatus = "";
        try
        {
            await service.StartAsync(options);
        }
        catch (Exception startError) when (IsLocalServiceError(startError))
        {
            DiscoveryStatus = DescribeError(startError, "Не удалось запустить процесс.");
        }
        finally
        {
            NotifyProcessState();
        }
    }

    internal async Task StopProcessAsync()
    {
        if (!CanStopProcess)
            return;

        DiscoveryStatus = "";
        discovery?.Cancel();
        try
        {
            await service.StopAsync();
        }
        catch (Exception error) when (IsLocalServiceError(error))
        {
            DiscoveryStatus = DescribeError(error, "Не удалось остановить процесс.");
        }
        finally
        {
            NotifyProcessState();
        }
    }

    internal void Apply()
    {
        if (!CanApply)
            return;

        var options = ReadDraft();
        if (Enabled && ValidateConfiguration(options) is { } error)
        {
            ApplyStatus = error;
            return;
        }

        settings.LocalAi = saved = options;
        settings.AutomaticCategorizationEnabled = savedEnabled = Enabled;
        DraftChanged();
    }

    internal void NotifyProcessState()
    {
        OnPropertyChanged(nameof(IsManaged));
        OnPropertyChanged(nameof(ShowProcess));
        OnPropertyChanged(nameof(CanStartProcess));
        OnPropertyChanged(nameof(CanStopProcess));
        OnPropertyChanged(nameof(ProcessStatus));
    }

    private void UpdateTarget(LocalAiOptions updated, string property)
    {
        var before = ReadDraft();
        draft = updated;
        OnPropertyChanged(property);
        TargetChanged(before);
    }

    private void TargetChanged(LocalAiOptions before)
    {
        if (!editing)
            return;

        var target = ModelSource(ReadDraft());
        if (target != ModelSource(before))
        {
            discovery?.Cancel();
            DiscoveryStatus = "";
            RestoreModel(target == ModelSource(saved) ? saved.Model : "");
        }
        DraftChanged();
    }

    private void DraftChanged()
    {
        ApplyStatus = "";
        NotifyActions();
    }

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanDiscover));
        NotifyProcessState();
    }

    private void RestoreModel(string model) => SetModels(model.Length == 0 ? [] : [model], model);

    private void SetModels(IReadOnlyList<string> models, string selected)
    {
        replacingModels = true;
        try
        {
            // Resetting the ItemsSource can briefly clear the selection in WPF.
            Models = models;
            OnPropertyChanged(nameof(Models));
            draft = draft with { Model = selected };
            OnPropertyChanged(nameof(SelectedModel));
        }
        finally
        {
            replacingModels = false;
        }
    }

    private LocalAiOptions ReadDraft() => draft with
    {
        Endpoint = draft.Endpoint.Trim(),
        ExecutablePath = TrimToNull(draft.ExecutablePath),
        ModelDirectory = draft.Provider == LocalAiProvider.LlamaCpp ? TrimToNull(draft.ModelDirectory) : null
    };

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ValidateConfiguration(LocalAiOptions options) =>
        ValidateTarget(options) ?? (options.Model.Length == 0 ? "Выберите модель." : null);

    private static (LocalAiProvider Provider, LocalAiMode Mode, string? Endpoint, string? Executable, string? Directory)
        ModelSource(LocalAiOptions options) => options.Mode == LocalAiMode.External
        ? (options.Provider, options.Mode, options.Endpoint, null, null)
        : (options.Provider, options.Mode, null, options.ExecutablePath, options.ModelDirectory);

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

    public sealed record Choice<T>(T Value, string Label);
}
