using Castar.Domain.Models.DataBase;
using Castar.Extensions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private const string TestPurchaseCallbackPrefix = "castar:buy:";

    internal async Task<bool> HandleTestPurchaseAsync(ITelegramBotClient bot, Update update,
        TestPurchaseSessions sessions, CancellationToken token)
    {
        if (update.CallbackQuery is { Data: { } callbackData } callback &&
            callbackData.StartsWith(TestPurchaseCallbackPrefix, StringComparison.Ordinal))
        {
            await HandleTestPurchaseCallbackAsync(bot, callback, callbackData, sessions, token);
            return true;
        }

        if ((update.Message ?? update.EditedMessage) is not { Text: { } input, From: { IsBot: false } sender } message ||
            settings.TelegramBot.TestChatId != message.Chat.Id)
            return false;

        var text = input.Trim();
        var command = text.Split('@', 2)[0];
        var start = command.Equals("/testpurchase", StringComparison.OrdinalIgnoreCase);
        var cancel = command.Equals("/cancel", StringComparison.OrdinalIgnoreCase);
        var draft = sessions.Find(message.Chat.Id, sender.Id);
        var nextReply = draft is { Amount: null } && message.ReplyToMessage?.MessageId == draft.PromptId;
        if (!start && !cancel && !nextReply)
            return false;

        if (update.EditedMessage is not null)
            return true;

        if (await usersService.GetUserByTelegramId(sender.Id, token) is null)
        {
            await ReplyAsync(bot, message, "Пользователь не зарегистрирован в Castar.", token);
            return true;
        }

        if (cancel)
        {
            if (sessions.Remove(message.Chat.Id, sender.Id))
                await ReplyAsync(bot, message, "Покупка отменена.", token);
            return true;
        }

        if (start)
        {
            draft = sessions.Start(message.Chat.Id, sender.Id);
            await AskAsync(bot, message, draft, "Название покупки", token);
            return true;
        }

        if (draft is null)
            return false;
        if (draft.Name is null)
        {
            if (text.Length is < 1 or > 400 || text.Any(char.IsControl))
            {
                await AskAsync(bot, message, draft, "Введите название длиной до 400 символов.", token);
                return true;
            }
            await AskAsync(bot, message, draft, "Сумма покупки", token);
            draft.Name = text;
            return true;
        }

        if (text.ToDecimal() is not { } amount || amount <= 0)
        {
            await AskAsync(bot, message, draft, "Введите положительную сумму.", token);
            return true;
        }

        var currencyButtons = settings.ManualCurrencies.Currencies
            .Select(code => Button(code, draft, code)).Chunk(4).Append([Button("Отмена", draft, "cancel")]);
        var currencyPrompt = await bot.SendMessage(message.Chat.Id, "Валюта покупки",
            replyMarkup: new InlineKeyboardMarkup(currencyButtons), messageThreadId: message.MessageThreadId,
            cancellationToken: token);
        draft.PromptId = currencyPrompt.MessageId;
        draft.Amount = amount;
        return true;
    }

    private async Task HandleTestPurchaseCallbackAsync(ITelegramBotClient bot, CallbackQuery callback,
        string data, TestPurchaseSessions sessions, CancellationToken token)
    {
        var chat = callback.Message?.Chat.Id;
        var user = callback.From.Id;
        var payload = data[TestPurchaseCallbackPrefix.Length..].Split(':', 2);
        if (chat is not { } chatId || settings.TelegramBot.TestChatId != chatId || callback.From.IsBot ||
            payload is not [var id, var action] || !Guid.TryParseExact(id, "N", out var draftId) ||
            sessions.Find(chatId, user) is not { Name: { } name, Amount: { } amount } draft ||
            draft.Id != draftId || callback.Message?.MessageId != draft.PromptId)
        {
            await bot.AnswerCallbackQuery(callback.Id, "Действие недоступно.", cancellationToken: token);
            return;
        }

        if (action == "cancel")
        {
            sessions.Remove(chatId, user);
            await bot.AnswerCallbackQuery(callback.Id, "Покупка отменена.", cancellationToken: token);
            return;
        }

        var currencies = settings.ManualCurrencies.Currencies;
        if (action == "save" && draft.Currency is { } code && currencies.Contains(code))
        {
            if (await usersService.GetUserByTelegramId(user, token) is null || settings.TelegramBot.TestChatId != chatId)
            {
                await bot.AnswerCallbackQuery(callback.Id, "Действие недоступно.", cancellationToken: token);
                return;
            }

            if (await purchasesService.GetPurchaseById(draft.Id) is null)
            {
                await purchasesService.Add(new Purchase
                {
                    Id = draft.Id, Name = name, Currency = code,
                    Items = new() { NewManualItem(name, amount) }
                }, user, token);
            }
            sessions.Remove(chatId, user);
            await bot.AnswerCallbackQuery(callback.Id, "Покупка сохранена.", cancellationToken: token);
            return;
        }

        if (draft.Currency is null && currencies.Contains(action))
        {
            var summary = await bot.SendMessage(chatId, $"{name}\n{FormatManualAmount(amount, action)}",
                replyMarkup: new InlineKeyboardMarkup(new[] { Button("Сохранить", draft, "save"), Button("Отмена", draft, "cancel") }),
                messageThreadId: callback.Message?.MessageThreadId, cancellationToken: token);
            draft.PromptId = summary.MessageId;
            draft.Currency = action;
            await bot.AnswerCallbackQuery(callback.Id, cancellationToken: token);
            return;
        }

        await bot.AnswerCallbackQuery(callback.Id, "Действие недоступно.", cancellationToken: token);
    }

    private static async Task AskAsync(ITelegramBotClient bot, Message message,
        TestPurchaseSessions.Draft draft, string prompt, CancellationToken token)
    {
        var sent = await bot.SendMessage(message.Chat.Id, prompt,
            replyParameters: new ReplyParameters { MessageId = message.MessageId },
            replyMarkup: new ForceReplyMarkup { Selective = true },
            messageThreadId: message.MessageThreadId, cancellationToken: token);
        draft.PromptId = sent.MessageId;
    }

    private static InlineKeyboardButton Button(string text, TestPurchaseSessions.Draft draft, string action) =>
        InlineKeyboardButton.WithCallbackData(text, $"{TestPurchaseCallbackPrefix}{draft.Id:N}:{action}");
}
