using Castar.BL;
using Castar.BL.Settings;
using Castar.Domain.Models.SettingsModel;
using Castar.Services.Receipts;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using BudgetUser = Castar.Domain.Models.DataBase.User;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler(
    ISettingsService settingsService,
    IPurchasesService purchasesService,
    IIncomesService incomesService,
    IUsersService usersService,
    IEnumerable<IReceiptProvider> receiptProviders)
{
    private readonly SettingsModel settings = settingsService.Get();
    private readonly IReceiptProvider[] providers = receiptProviders.ToArray();

    internal async Task HandleAsync(ITelegramBotClient bot, Message message, bool edited, CancellationToken cancellationToken)
    {
        if (message is not { From: { IsBot: false } sender, Chat.Type: ChatType.Private }
            || (message.Text is null && message.Photo is null))
            return;

        var lines = message.Text?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
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

        if (message.Photo is not null || lines.Any(IsReceiptContent))
        {
            await ImportReceiptsAsync(bot, message, user, lines, cancellationToken);
            return;
        }

        try
        {
            var firstWord = lines.FirstOrDefault()?.Split(' ', '\t')[0];
            var result = string.Equals(firstWord, "/income", StringComparison.OrdinalIgnoreCase)
                ? await SaveIncomesAsync(message, user, lines, edited, cancellationToken)
                : await SaveManualPurchaseAsync(message, user, lines, edited, cancellationToken);

            if (!string.IsNullOrEmpty(result))
                await NotifyUsersAsync(bot, user, result, cancellationToken);
        }
        catch (FormatException ex)
        {
            await ReplyAsync(bot, message, ex.Message, cancellationToken);
        }
    }

    private bool IsReceiptContent(string line) =>
        providers.Any(provider => provider.CanHandle(line)) ||
        line.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private async Task NotifyUsersAsync(ITelegramBotClient bot, BudgetUser actor, string text, CancellationToken cancellationToken)
    {
        var users = await usersService.GetAllUsersWithTelegramId(cancellationToken);
        foreach (var telegramId in users.Select(user => user.TelegramId).Append(actor.TelegramId).Distinct())
        {
            try
            {
                await SendTextAsync(bot, telegramId, text, cancellationToken);
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
