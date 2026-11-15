using System.Text;
using Castar.Domain.Models.DataBase;
using Castar.Services.Receipts;
using Castar.Services.Receipts.Barcode;
using Castar.Services.Receipts.Exceptions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using BudgetUser = Castar.Domain.Models.DataBase.User;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private async Task ImportReceiptsAsync(
        ITelegramBotClient bot, Message message, BudgetUser user, string[] codes, CancellationToken cancellationToken)
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

            codes = [code];
        }

        foreach (var code in codes.Distinct(StringComparer.Ordinal))
        {
            if (!provider.CanHandle(code))
            {
                await ReplyAsync(bot, message, "Код чека не поддерживается выбранным поставщиком.", cancellationToken);
                continue;
            }

            Receipt receipt;
            try
            {
                receipt = await provider.GetReceiptAsync(code, cancellationToken);
            }
            catch (TicketNotReadyException)
            {
                await ReplyAsync(bot, message, "Поставщик ещё обрабатывает чек. Попробуйте позже.", cancellationToken);
                continue;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
            {
                await ReplyAsync(bot, message, ex.Message, cancellationToken);
                continue;
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TimeoutException
                || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                CastarTelegramBotClient.LogError(ex);
                await ReplyAsync(bot, message, "Поставщик чеков временно недоступен. Попробуйте позже.", cancellationToken);
                continue;
            }

            var purchase = new Purchase
            {
                MessageId = message.MessageId,
                Name = receipt.Name,
                RetailPlace = receipt.RetailPlace,
                RetailPlaceAddress = receipt.RetailPlaceAddress,
                AddedOn = receipt.IssuedAt.LocalDateTime,
                ReceiptDate = receipt.OperationDate,
                Currency = receipt.Currency,
                TotalSum = receipt.Total,
                RawData = receipt.LegacyVerificationCode is { } legacyCode ? Encoding.UTF8.GetBytes(legacyCode) : null,
                Items = new(receipt.Items.Select(item => new Item
                {
                    Name = item.Name,
                    Price = item.UnitPrice,
                    Quantity = (double)item.Quantity,
                    Sum = item.Total
                }))
            };

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

            await NotifyUsersAsync(bot, user, result.ToString(), cancellationToken);
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
            if (decoder.Decode(stream, cancellationToken)?.Trim() is { Length: > 0 } code)
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
