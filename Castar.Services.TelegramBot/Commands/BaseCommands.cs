using Castar.BL;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.Receipts;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler(
    SettingsModel settings,
    IPurchasesService purchasesService,
    IIncomesService incomesService,
    IUsersService usersService,
    IEnumerable<IReceiptProvider> receiptProviders)
{
    private readonly IReceiptProvider[] providers = receiptProviders.ToArray();

    internal async Task HandleAsync(ITelegramBotClient bot, Message message, bool edited, CancellationToken cancellationToken)
    {
        if (message is not { From: { IsBot: false } sender, Chat.Type: ChatType.Private }
            || (message.Text is null && message.Photo is null && message.Document is null))
            return;

        var sourceLines = message.Text?.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray() ?? [];
        var lines = sourceLines.Select(line => line.Trim()).ToArray();
        if (lines.Length == 1 && lines[0].Equals("/me", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(bot, message, $"{sender.Username ?? sender.FirstName}: {sender.Id}", cancellationToken);
            return;
        }

        var user = await usersService.GetUserByTelegramId(sender.Id, cancellationToken);
        if (user is null)
        {
            await ReplyAsync(bot, message, $"Пользователь с Telegram ID {sender.Id} не зарегистрирован.", cancellationToken);
            return;
        }

        if (message.Photo is not null || message.Document is not null || lines.Any(IsReceiptContent))
        {
            await ImportReceiptsAsync(bot, message, user, sourceLines, cancellationToken);
            return;
        }

        try
        {
            var firstWord = lines.FirstOrDefault()?.Split(' ', '\t')[0];
            var result = string.Equals(firstWord, "/income", StringComparison.OrdinalIgnoreCase)
                ? await SaveIncomesAsync(message, user, lines, edited, cancellationToken)
                : await SaveManualPurchaseAsync(bot, message, user, lines, edited, cancellationToken);

            if (!string.IsNullOrEmpty(result))
                await NotifyUsersAsync(bot, result, cancellationToken);
        }
        catch (FormatException ex)
        {
            await ReplyAsync(bot, message, ex.Message, cancellationToken);
        }
    }

    private bool IsReceiptContent(string line) =>
        providers.Any(provider => RecognizesReceipt(provider, line)) ||
        (!line.Any(char.IsWhiteSpace) &&
            (line.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
             line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)));

    private static bool RecognizesReceipt(IReceiptProvider provider, string line)
    {
        try
        {
            return provider.CanHandle(line);
        }
        catch (NotImplementedException)
        {
            return false;
        }
    }

    private async Task NotifyUsersAsync(ITelegramBotClient bot, string text, CancellationToken cancellationToken)
    {
        var users = await usersService.GetAllUsersWithTelegramId(cancellationToken);
        var recipients = users.GroupBy(user => user.TelegramId)
            .Where(accounts => accounts.All(user => user.TelegramNotificationsEnabled));
        foreach (var recipient in recipients)
        {
            try
            {
                await SendTextAsync(bot, recipient.Key, text, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                CastarTelegramBotClient.LogError(ex);
            }
        }
    }

    private static Task ReplyAsync(ITelegramBotClient bot, Message message, string text, CancellationToken cancellationToken) =>
        SendTextAsync(bot, message.Chat.Id, text, cancellationToken);

    private static async Task SendTextAsync(ITelegramBotClient bot, long chatId, string text, CancellationToken cancellationToken)
    {
        const int maxLength = 4096;
        while (text.Length > maxLength)
        {
            var splitAt = text.LastIndexOf('\n', maxLength - 1, maxLength);
            if (splitAt <= 0)
                splitAt = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;

            await bot.SendMessage(chatId, text[..splitAt], cancellationToken: cancellationToken);
            text = text[splitAt..].TrimStart('\n');
        }

        if (text.Length > 0)
            await bot.SendMessage(chatId, text, cancellationToken: cancellationToken);
    }
}
