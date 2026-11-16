using Castar.Api;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Contents;

internal sealed class ServerUpdateViewModel : BaseSettingsModel
{
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(75);

    private readonly CastarServerClient server;
    private readonly BudgetEventConnection? events;
    private (string Address, string Key)? connection;
    private CancellationTokenSource? request;
    private CancellationTokenSource? restartWait;
    private string? targetVersion;
    private string? lastState;
    private long statusGeneration;
    private bool active;
    private bool unsupported;
    private bool receivesEvents;
    private bool hasSnapshot;
    private bool starting;
    private string status = "";
    private bool isWorking;
    private bool isIndeterminate;
    private int percent;

    public ServerUpdateViewModel(CastarServerClient server, BudgetEventConnection? events = null)
    {
        this.server = server;
        this.events = events;
        if (events is not null)
        {
            events.UpdateStatusChanged += ReceiveStatus;
            events.UpdateEventsUnavailable += EventsUnavailable;
            events.Disconnected += ConnectionLost;
        }
    }

    public bool CanStart => active && connection is not null && request is null && !IsWorking && !unsupported;
    public string Status { get => status; private set => SetField(ref status, value); }
    public bool IsWorking { get => isWorking; private set => SetField(ref isWorking, value); }
    public bool IsIndeterminate { get => isIndeterminate; private set => SetField(ref isIndeterminate, value); }
    public int Percent { get => percent; private set => SetField(ref percent, value); }

    public void SetConnection(string address, string? protectedKey)
    {
        (string Address, string Key)? next = string.IsNullOrEmpty(protectedKey) ? null : (address, protectedKey);
        if (connection == next)
            return;

        CancelRequest();
        CancelRestartWait();
        connection = next;
        statusGeneration++;
        unsupported = false;
        receivesEvents = false;
        hasSnapshot = false;
        ForgetOperation();
        Status = "";
        OnPropertyChanged(nameof(CanStart));
    }

    public Task OpenAsync(string address, string? protectedKey)
    {
        active = true;
        SetConnection(address, protectedKey);
        OnPropertyChanged(nameof(CanStart));
        return connection is not null && request is null && !hasSnapshot ? RequestAsync(start: false) : Task.CompletedTask;
    }

    public void Close()
    {
        active = false;
        if (!starting)
            CancelRequest();
        if (!receivesEvents)
            hasSnapshot = false;
        OnPropertyChanged(nameof(CanStart));
    }

    public Task StartAsync() => CanStart ? RequestAsync(start: true) : Task.CompletedTask;

    private async Task RequestAsync(bool start)
    {
        using var current = new CancellationTokenSource();
        request = current;
        starting = start;
        var generation = statusGeneration;
        if (start)
        {
            ForgetOperation();
            ShowWorking("Проверка версии сервера…");
        }
        OnPropertyChanged(nameof(CanStart));
        try
        {
            var update = start ? await server.StartServerUpdateAsync(current.Token)
                : await server.GetServerUpdateAsync(current.Token);
            if (request == current && generation == statusGeneration)
            {
                hasSnapshot = receivesEvents;
                ApplyStatus(update, fromEvent: false);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            if (request == current && generation == statusGeneration)
            {
                unsupported = error is HttpRequestException { StatusCode: HttpStatusCode.NotFound };
                if (start || !IsWorking)
                {
                    ForgetOperation();
                    Status = unsupported ? "Этот сервер не поддерживает обновление из приложения."
                        : CastarServerClient.DescribeError(error);
                }
            }
        }
        finally
        {
            if (request == current)
            {
                request = null;
                starting = false;
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    private void ReceiveStatus(ServerUpdateStatusDto? update)
    {
        if (connection is null)
            return;
        receivesEvents = true;
        hasSnapshot = true;
        ApplyStatus(update, fromEvent: true);
    }

    private void ApplyStatus(ServerUpdateStatusDto? update, bool fromEvent)
    {
        // A delayed idle snapshot must not eclipse a command still awaiting its acknowledgement.
        if (fromEvent && starting && update is { State: "idle" or "current" } && lastState is null)
            return;

        if (fromEvent)
            statusGeneration++;
        CancelRestartWait();
        unsupported = update is null;
        if (update is null)
        {
            ForgetOperation();
            Status = "Этот сервер не поддерживает обновление из приложения.";
        }
        else if (lastState == "installing" && (update.State is "idle" or "current") && targetVersion is { } target
            && !SameVersion(target, update.InstalledVersion))
        {
            ForgetOperation();
            Status = $"Сервер ответил с версией {update.InstalledVersion}. Обновление до {target} не подтверждено.";
        }
        else if (ShowStatus(update) && !receivesEvents && events is not null && events.IsSynchronized)
        {
            Status += " Ход обновления доступен при повторном открытии настроек.";
        }
        OnPropertyChanged(nameof(CanStart));
    }

    private void EventsUnavailable()
    {
        receivesEvents = false;
        hasSnapshot = false;
        if (active && connection is not null && request is null)
            _ = RequestAsync(start: false);
    }

    private void ConnectionLost()
    {
        hasSnapshot = false;
        if (!IsWorking || restartWait is not null)
            return;

        var current = new CancellationTokenSource();
        restartWait = current;
        ShowWorking(lastState == "installing"
            ? "Сервер перезапускается. Ожидание подключения…"
            : "Ожидание ответа сервера…");
        _ = WaitForRestartAsync(current);
    }

    private async Task WaitForRestartAsync(CancellationTokenSource current)
    {
        using (current)
        {
            try
            {
                await Task.Delay(ReconnectTimeout, current.Token);
                if (restartWait == current)
                {
                    restartWait = null;
                    ForgetOperation();
                    Status = "Сервер не восстановил соединение. Статус обновления неизвестен.";
                    OnPropertyChanged(nameof(CanStart));
                }
            }
            catch (OperationCanceledException) when (current.IsCancellationRequested)
            {
            }
        }
    }

    private void CancelRestartWait()
    {
        var previous = restartWait;
        restartWait = null;
        previous?.Cancel();
    }


    private bool ShowStatus(ServerUpdateStatusDto update)
    {
        if (string.IsNullOrWhiteSpace(update.InstalledVersion))
        {
            Status = "Сервер не сообщил установленную версию.";
            ForgetOperation();
            return false;
        }

        switch (update.State)
        {
            case "checking":
                lastState = update.State;
                ShowWorking("Проверка версии сервера…");
                return true;
            case "downloading":
                lastState = update.State;
                targetVersion = update.Version ?? targetVersion;
                var downloaded = Math.Max(0, update.DownloadedBytes);
                var total = Math.Max(0, update.TotalBytes);
                if (total == 0)
                    ShowWorking($"Загружено {FormatMiB(downloaded)}");
                else
                {
                    var progress = (int)(100d * Math.Min(downloaded, total) / total);
                    ShowWorking($"Загружено {FormatMiB(downloaded)} из {FormatMiB(total)} ({progress} %)", progress);
                }
                return true;
            case "installing":
                lastState = update.State;
                targetVersion = update.Version ?? targetVersion;
                ShowWorking("Установка обновления. Сервер может ненадолго стать недоступным.");
                return true;
            case "failed":
                Status = string.IsNullOrWhiteSpace(update.Error)
                    ? "Сервер не смог применить обновление."
                    : $"Сбой обновления. {update.Error}";
                break;
            case "complete":
                var expected = targetVersion ?? update.Version;
                Status = expected is null || SameVersion(expected, update.InstalledVersion)
                    ? $"Сервер обновлён до версии {update.InstalledVersion}."
                    : $"Сервер ответил с версией {update.InstalledVersion}. Обновление до {expected} не подтверждено.";
                break;
            case "current":
            case "idle":
                Status = targetVersion is not null && SameVersion(targetVersion, update.InstalledVersion)
                    ? $"Сервер обновлён до версии {update.InstalledVersion}."
                    : update.State == "current"
                        ? $"Версия сервера {update.InstalledVersion} актуальна."
                        : $"Версия сервера {update.InstalledVersion}.";
                break;
            default:
                Status = "Сервер вернул неизвестное состояние обновления.";
                break;
        }
        ForgetOperation();
        return false;
    }

    private void ShowWorking(string message, int? progress = null)
    {
        Status = message;
        Percent = progress.GetValueOrDefault();
        IsIndeterminate = progress is null;
        IsWorking = true;
    }

    private void CancelRequest()
    {
        var previous = request;
        request = null;
        starting = false;
        previous?.Cancel();
    }

    private void ForgetOperation()
    {
        targetVersion = null;
        lastState = null;
        IsWorking = false;
        IsIndeterminate = false;
        Percent = 0;
    }

    private static string FormatMiB(long bytes) => $"{bytes / (1024d * 1024d):0.##} МиБ";

    private static bool SameVersion(string expected, string? installed) => string.Equals(
        expected.Split('+', 2)[0].TrimStart('v', 'V'), installed?.Split('+', 2)[0].TrimStart('v', 'V'),
        StringComparison.OrdinalIgnoreCase);
}
