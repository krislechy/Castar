using System.Text;
using Castar.Services.Receipts;
using Castar.Services.Receipts.Barcode;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using BudgetUser = Castar.Domain.Models.DataBase.User;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private async Task ImportReceiptsAsync(
        ITelegramBotClient bot, Message message, BudgetUser user, string[] sources, CancellationToken cancellationToken)
    {
        var receiptSettings = settings.Receipts;
        if (!receiptSettings.IsEnabled)
        {
            await ReplyAsync(bot, message, "Импорт чеков отключён в настройках.", cancellationToken);
            return;
        }

        var provider = providers.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, receiptSettings.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            await ReplyAsync(bot, message, "В настройках выбран недоступный поставщик чеков.", cancellationToken);
            return;
        }

        if (message.Photo is { Length: > 0 } || message.Document is not null)
        {
            if (message.Document?.FileSize > QrCode.MaxImageBytes)
            {
                await ReplyAsync(bot, message, "Отправьте изображение размером до 20 МБ.", cancellationToken);
                return;
            }

            var code = await DecodeImageAsync(bot, message, cancellationToken);
            if (code is null)
            {
                await ReplyAsync(bot, message,
                    "QR-код чека не удалось прочитать. Отправьте изображение как файл или пришлите ссылку из QR-кода.",
                    cancellationToken);
                return;
            }

            sources = [code];
        }

        foreach (var source in sources.DistinctBy(source => source.Trim(), StringComparer.Ordinal))
        {
            if (receiptQueue is not null && await receiptQueue.ContainsAsync(provider.Id, source, cancellationToken))
            {
                await ReplyAsync(bot, message, "Этот чек уже в очереди.", cancellationToken);
                continue;
            }

            Receipt receipt;
            try
            {
                receipt = await provider.GetReceiptAsync(source, cancellationToken);
            }
            catch (Exception ex) when (ReceiptProviderFailure.IsTemporary(ex, cancellationToken))
            {
                if (receiptQueue is null)
                {
                    await ReplyAsync(bot, message, "Поставщик чеков временно недоступен. Попробуйте позже.", cancellationToken);
                    continue;
                }
                var request = new ReceiptRetryRequest(provider.Id, source, user.Id, user.TelegramId,
                    message.Chat.Id, message.MessageId);
                var retryAfter = (ex as ReceiptProviderUnavailableException)?.RetryAfterUtc;
                if (await receiptQueue.EnqueueAsync(request, retryAfter, cancellationToken))
                {
                    var acknowledgement = await bot.SendMessage(message.Chat.Id,
                        "Чек в очереди. Повторим запрос автоматически.", replyParameters: message.MessageId,
                        cancellationToken: cancellationToken);
                    await receiptQueue.SetAcknowledgementAsync(provider.Id, source, acknowledgement.MessageId, cancellationToken);
                }
                else
                    await ReplyAsync(bot, message, "Этот чек уже в очереди.", cancellationToken);
                continue;
            }
            catch (Exception ex) when (ReceiptProviderFailure.GetPermanentMessage(ex) is { } error)
            {
                await ReplyAsync(bot, message, error, cancellationToken);
                continue;
            }

            var purchase = ReceiptPurchase.Create(receipt, source, message.MessageId);
            if (await purchasesService.AddReceipt(purchase, user.TelegramId, provider.Id, receipt.Id, cancellationToken) is null)
            {
                await ReplyAsync(bot, message, "Этот чек уже добавлен.", cancellationToken);
                continue;
            }

            var result = new StringBuilder()
                .AppendLine($"{settings.TelegramBot.PurchaseCreateEmoji} {user.Name} добавил(а) покупку [{receipt.Name}]")
                .AppendLine($"{receipt.RetailPlace} / {receipt.RetailPlaceAddress}")
                .AppendLine($"Итого: {receipt.Total:F2} {receipt.Currency}");
            foreach (var item in receipt.Items)
                result.AppendLine($"{settings.TelegramBot.ItemEmoji} {item.Name} × {item.Quantity} — {item.Total:F2} {receipt.Currency}");

            await NotifyUsersAsync(bot, result.ToString(), cancellationToken);
        }
    }

    private static async Task<string?> DecodeImageAsync(
        ITelegramBotClient bot, Message message, CancellationToken cancellationToken)
    {
        IEnumerable<(string Id, long? Size)> files = message.Document is { } document
            ? [(document.FileId, document.FileSize)]
            : (message.Photo ?? []).OrderByDescending(photo => (long)photo.Width * photo.Height)
                .Take(3).Select(photo => (photo.FileId, photo.FileSize));

        var decoder = new QrCode();
        foreach (var (fileId, size) in files)
        {
            if (size > QrCode.MaxImageBytes)
                continue;

            using var stream = new ImageBuffer();
            try
            {
                await bot.GetInfoAndDownloadFile(fileId, stream, cancellationToken);
            }
            catch (RequestException error) when (error.InnerException is InvalidDataException)
            {
                continue;
            }
            catch (RequestException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            stream.Position = 0;
            var code = decoder.Decode(stream, cancellationToken);
            if (!string.IsNullOrWhiteSpace(code))
                return code;
        }

        return null;
    }

    private sealed class ImageBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLength(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLength(buffer.Length);
            base.Write(buffer);
        }

        private void CheckLength(int count)
        {
            if (Position + count > QrCode.MaxImageBytes)
                throw new InvalidDataException("Файл Telegram превышает допустимый размер.");
        }
    }
}
