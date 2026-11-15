using Castar.Api;
using Castar.Domain.Models.SettingsModel;
using Castar.ServerConnection;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.Contents;

internal sealed class ServerUpdateViewModel(CastarServerClient server) : BaseSettingsModel
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(75);

    private (string Address, string Key)? connection;
    private CancellationTokenSource? request;
    private string? targetVersion;
    private string? lastState;
    private bool active;
    private bool unsupported;
    private string status = "";
    private bool isWorking;
    private bool isIndeterminate;
    private int percent;

    public bool CanStart => active && connection is not null && request is null && !unsupported;
    public string Status { get => status; private set => SetField(ref status, value); }
    public bool IsWorking { get => isWorking; private set => SetField(ref isWorking, value); }
    public bool IsIndeterminate { get => isIndeterminate; private set => SetField(ref isIndeterminate, value); }
    public int Percent { get => percent; private set => SetField(ref percent, value); }

    public Task OpenAsync(string address, string? protectedKey)
    {
        active = true;
        (string Address, string Key)? next = string.IsNullOrEmpty(protectedKey) ? null : (address, protectedKey);
        if (connection != next)
        {
            CancelRequest();
            connection = next;
            unsupported = false;
            ForgetOperation();
            Status = "";
        }

        OnPropertyChanged(nameof(CanStart));
        return connection is not null && request is null ? PollAsync(start: false) : Task.CompletedTask;
    }

    public void Close()
    {
        active = false;
        CancelRequest();
        IsWorking = false;
        OnPropertyChanged(nameof(CanStart));
    }

    public Task StartAsync() => CanStart ? PollAsync(start: true) : Task.CompletedTask;

    private async Task PollAsync(bool start)
    {
        using var current = new CancellationTokenSource();
        request = current;
        OnPropertyChanged(nameof(CanStart));
        if (start)
        {
            ForgetOperation();
            ShowWorking("Проверка версии сервера…");
        }

        long? reconnectSince = null;
        try
        {
            while (request == current)
            {
                try
                {
                    var operation = start ? server.StartServerUpdateAsync(current.Token) : server.GetServerUpdateAsync(current.Token);
                    start = false;
                    var update = await operation;
                    if (request != current)
                        return;

                    unsupported = false;
                    if (lastState == "installing" && (update.State is "idle" or "current")
                        && targetVersion is not null && !SameVersion(targetVersion, update.InstalledVersion))
                    {
                        // A replacement process or its proxy may answer before its version is confirmed.
                        if (!WaitForRestart(ref reconnectSince))
                        {
                            Status = $"Сервер ответил с версией {update.InstalledVersion}. Обновление до {targetVersion} не подтверждено.";
                            return;
                        }
                    }
                    else
                    {
                        reconnectSince = null;
                        if (!ShowStatus(update))
                            return;
                    }
                }
                catch (Exception error) when (CastarServerClient.IsConnectionError(error))
                {
                    if (request != current)
                        return;

                    if (!IsTemporaryDisconnect(error) || !WaitForRestart(ref reconnectSince))
                    {
                        unsupported = error is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                            && lastState is null;
                        Status = unsupported
                            ? "Этот сервер не поддерживает обновление из приложения."
                            : CastarServerClient.DescribeError(error);
                        return;
                    }
                }

                await Task.Delay(PollInterval, current.Token);
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            // Hiding this view stops status polling. The server can finish independently.
        }
        finally
        {
            if (request == current)
            {
                request = null;
                IsWorking = false;
                OnPropertyChanged(nameof(CanStart));
            }
        }
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

    private bool IsTemporaryDisconnect(Exception error)
    {
        if (lastState is null)
            return false;

        return error is OperationCanceledException { InnerException: TimeoutException }
            || error is HttpRequestException
            {
                StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
            }
            || error is HttpRequestException
            {
                StatusCode: null,
                HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded
            }
            || (lastState == "installing" && error is HttpRequestException { StatusCode: HttpStatusCode.NotFound });
    }

    private bool WaitForRestart(ref long? reconnectSince)
    {
        reconnectSince ??= Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(reconnectSince.Value) >= ReconnectTimeout)
            return false;

        ShowWorking(lastState == "installing"
            ? "Сервер перезапускается. Ожидание подключения…"
            : "Ожидание ответа сервера…");
        return true;
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
