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
    private static readonly UpdateType[] AllowedUpdates = [UpdateType.Message, UpdateType.EditedMessage];

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
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            var bot = new TelegramBotClient(configuration.Token!, httpClient, cancellationToken);
            int? offset = null;
            PublishStatus(configuration, "Подключение...");
            while (!cancellationToken.IsCancellationRequested)
            {
                Update[] updates;
                try
                {
                    updates = await bot.GetUpdates(offset, timeout: 30, allowedUpdates: AllowedUpdates,
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
                    PublishStatus(configuration, "Ошибка подключения к Telegram");
                    var retryAfter = (ex as ApiRequestException)?.Parameters?.RetryAfter ?? 0;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, retryAfter)), cancellationToken);
                    continue;
                }

                PublishStatus(configuration, "Подключено");
                foreach (var update in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await HandleUpdateAsync(bot, update, cancellationToken);
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

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken cancellationToken)
    {
        var message = update.Message ?? update.EditedMessage;
        if (message is null)
            return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CommandHandler>();
            await handler.HandleAsync(client, message, update.EditedMessage is not null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(ex);
            if (message.Chat.Type == ChatType.Private)
            {
                try
                {
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
