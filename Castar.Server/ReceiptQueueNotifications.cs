using Castar.DAL;
using Castar.Domain.Models.SettingsModel;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Castar.Server;

internal sealed class ReceiptQueueNotifications(SettingsModel settings,
    ILogger<ReceiptQueueNotifications> logger, HttpClient httpClient) : IDisposable
{
    private static readonly TimeSpan DefaultRetry = TimeSpan.FromMinutes(1);

    public async Task<TimeSpan?> DeliverAsync(QueuedReceipt receipt, CancellationToken cancellationToken)
    {
        var token = settings.TelegramBot.TelegramBotApi_KEY?.Trim();
        if (!settings.Systems.IsEnableTelegramBot || string.IsNullOrEmpty(token))
            return DefaultRetry;

        var bot = new TelegramBotClient(token, httpClient);
        var result = receipt.Result!;
        try
        {
            if (receipt.AcknowledgementMessageId is { } acknowledgement)
            {
                try
                {
                    await bot.EditMessageText(receipt.ChatId, acknowledgement, result, cancellationToken: cancellationToken);
                    return null;
                }
                catch (ApiRequestException error) when (error.ErrorCode == 400)
                {
                    if (error.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
                        return null;
                }
            }

            await bot.SendMessage(receipt.ChatId, result, replyParameters: new ReplyParameters
            {
                MessageId = receipt.MessageId,
                AllowSendingWithoutReply = true
            }, cancellationToken: cancellationToken);
            return null;
        }
        catch (ApiRequestException error) when (error.ErrorCode is 400 or 403)
        {
            logger.LogWarning("Telegram отклонил доставку результата импорта чека ({Status}).", error.ErrorCode);
            return null;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Сбой доставки результата импорта чека в Telegram ({ErrorType}).", error.GetType().Name);
            var retryAfterSeconds = (error as ApiRequestException)?.Parameters?.RetryAfter ?? 0;
            return TimeSpan.FromSeconds(Math.Max(DefaultRetry.TotalSeconds, retryAfterSeconds));
        }
    }

    public void Dispose() => httpClient.Dispose();
}
