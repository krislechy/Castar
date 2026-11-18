using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.TelegramBot.Commands;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramSettings = Castar.Domain.Models.SettingsModel.TelegramBot;

namespace Castar.Services.TelegramBot;

public sealed class CastarTelegramBotClient : ICastarTelegramBotClient
{
    private static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.EditedMessage, UpdateType.CallbackQuery];

    private readonly SettingsModel settings;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly Channel<Configuration> changes = Channel.CreateBounded<Configuration>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task worker;
    private long configurationVersion;
    private int disposed;

    public CastarTelegramBotClient(SettingsModel settings, IServiceScopeFactory scopeFactory)
    {
        this.settings = settings;
        this.scopeFactory = scopeFactory;
        settings.TelegramBot.PropertyChanged += OnSettingsChanged;
        settings.Systems.PropertyChanged += OnSettingsChanged;
        QueueConfiguration();
        worker = Task.Run(RunAsync);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Systems.IsEnableTelegramBot) or nameof(TelegramSettings.TelegramBotApi_KEY))
            QueueConfiguration();
    }

    private void QueueConfiguration() => changes.Writer.TryWrite(new(
        Interlocked.Increment(ref configurationVersion),
        settings.Systems.IsEnableTelegramBot,
        settings.TelegramBot.TelegramBotApi_KEY?.Trim()));

    private async Task RunAsync()
    {
        CancellationTokenSource? pollingCancellation = null;
        Task polling = Task.CompletedTask;

        try
        {
            await foreach (var configuration in changes.Reader.ReadAllAsync())
            {
                if (pollingCancellation is not null)
                {
                    pollingCancellation.Cancel();
                    await polling;
                    pollingCancellation.Dispose();
                    pollingCancellation = null;
                }

                if (Volatile.Read(ref disposed) != 0)
                    return;

                if (!configuration.Enabled)
                {
                    PublishStatus(configuration, "Отключено");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(configuration.Token))
                {
                    PublishStatus(configuration, "Токен не заполнен");
                    continue;
                }

                pollingCancellation = new CancellationTokenSource();
                polling = PollAsync(configuration, pollingCancellation.Token);
            }
        }
        finally
        {
            if (pollingCancellation is not null)
            {
                using (pollingCancellation)
                {
                    pollingCancellation.Cancel();
                    await polling;
                }
            }
        }
    }

    private async Task PollAsync(Configuration configuration, CancellationToken cancellationToken)
    {
        try
        {
            using var transport = new HttpClientHandler();
            using var httpClient = new HttpClient(transport, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(45) };
            using var pollHttpClient = new HttpClient(transport, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(65) };
            var bot = new TelegramBotClient(configuration.Token!, httpClient, cancellationToken);
            // The polling loop applies RetryAfter and backoff. Other bot operations keep the SDK's policy.
            var poller = new TelegramBotClient(new TelegramBotClientOptions(configuration.Token!) { RetryThreshold = 0 },
                pollHttpClient, cancellationToken);
            var purchases = new PurchaseWizardSessions();
            int? offset = null;
            var failures = 0;
            PublishStatus(configuration, "Подключение...");
            var menu = await BotMenu.ConfigureAsync(bot, cancellationToken);
            var nextMenuAttempt = DateTimeOffset.UtcNow.AddMinutes(1);
            while (!cancellationToken.IsCancellationRequested)
            {
                Update[] updates;
                try
                {
                    updates = await poller.GetUpdates(offset, timeout: 50, allowedUpdates: AllowedUpdates,
                        cancellationToken: cancellationToken);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode is 401 or 404)
                {
                    PublishStatus(configuration, "Проверьте токен Telegram");
                    return;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    LogError(ex);
                    var apiError = ex as ApiRequestException;
                    PublishStatus(configuration, apiError?.ErrorCode == 409
                        ? "Конфликт подключения Telegram" : "Ошибка подключения к Telegram");
                    var delay = PollRetryDelay(failures, apiError?.ErrorCode, apiError?.Parameters?.RetryAfter);
                    failures = Math.Min(failures + 1, 5);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                failures = 0;
                PublishStatus(configuration, "Подключено");
                if (!menu.Configured && DateTimeOffset.UtcNow >= nextMenuAttempt)
                {
                    var retry = await BotMenu.ConfigureAsync(bot, cancellationToken);
                    menu = (retry.Username ?? menu.Username, retry.Configured);
                    nextMenuAttempt = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                foreach (var update in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await HandleUpdateAsync(bot, update, purchases, menu.Username, cancellationToken);
                    offset = update.Id + 1;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ArgumentException)
        {
            PublishStatus(configuration, "Проверьте токен Telegram");
        }
        catch (Exception ex)
        {
            LogError(ex);
            PublishStatus(configuration, "Ошибка подключения к Telegram");
        }
    }

    internal static TimeSpan PollRetryDelay(int failures, int? statusCode, int? retryAfterSeconds)
    {
        const int maximumSeconds = 300;
        var baseSeconds = Math.Max(10 * (1 << Math.Min(failures, 5)), statusCode == 409 ? 60 : 0);
        var localDelay = Math.Min(maximumSeconds, baseSeconds * (1 + Random.Shared.NextDouble() / 5));
        return TimeSpan.FromSeconds(Math.Max(localDelay, retryAfterSeconds ?? 0));
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, PurchaseWizardSessions purchases,
        string? botUsername, CancellationToken cancellationToken)
    {
        var message = update.Message ?? update.EditedMessage;
        if (message is null && update.CallbackQuery is null)
            return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CommandHandler>();
            if (await handler.HandlePurchaseWizardAsync(client, update, purchases, botUsername, cancellationToken))
                return;
            if (message is not null)
                await handler.HandleAsync(client, message, update.EditedMessage is not null, cancellationToken, botUsername);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex);
            if (message?.Chat.Type == ChatType.Private || update.CallbackQuery is not null)
            {
                try
                {
                    if (update.CallbackQuery is { } callback)
                        await client.AnswerCallbackQuery(callback.Id, "Не удалось обработать действие.", cancellationToken: cancellationToken);
                    else if (message is not null)
                        await client.SendMessage(message.Chat.Id,
                            "Не удалось обработать сообщение. Проверьте подключение и настройки приложения.",
                            cancellationToken: cancellationToken);
                }
                catch (Exception sendError) when (!cancellationToken.IsCancellationRequested)
                {
                    LogError(sendError);
                }
            }
        }
    }

    private void PublishStatus(Configuration configuration, string status)
    {
        if (Volatile.Read(ref disposed) == 0 && Volatile.Read(ref configurationVersion) == configuration.Version)
            settings.TelegramBot.Status = status;
    }

    internal static void LogError(Exception exception)
    {
        var description = exception is ApiRequestException apiError
            ? $"Telegram API error {apiError.ErrorCode}"
            : exception.GetType().Name;
        Trace.TraceWarning("Castar Telegram: {0}", description);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            settings.TelegramBot.PropertyChanged -= OnSettingsChanged;
            settings.Systems.PropertyChanged -= OnSettingsChanged;
            changes.Writer.TryComplete();
        }

        await worker.ConfigureAwait(false);
    }

    private sealed record Configuration(long Version, bool Enabled, string? Token);
}
