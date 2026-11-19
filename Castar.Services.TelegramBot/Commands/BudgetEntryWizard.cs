using Castar.Domain.Models.DataBase;
using Castar.Extensions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Conversation = Castar.Services.TelegramBot.Commands.BudgetEntrySessions.Conversation;
using Draft = Castar.Services.TelegramBot.Commands.BudgetEntrySessions.Draft;
using EntryKind = Castar.Services.TelegramBot.Commands.BudgetEntrySessions.EntryKind;

namespace Castar.Services.TelegramBot.Commands;

public sealed partial class CommandHandler
{
    private const string PurchaseCallbackPrefix = "castar:buy:";
    private const string IncomeCallbackPrefix = "castar:income:";

    internal async Task<bool> HandleBudgetEntryWizardAsync(ITelegramBotClient bot, Update update,
        BudgetEntrySessions sessions, string? botUsername, CancellationToken token)
    {
        if (update.CallbackQuery is { Data: { } callbackData } callback)
        {
            var kind = callbackData.StartsWith(IncomeCallbackPrefix, StringComparison.Ordinal) ? EntryKind.Income
                : callbackData.StartsWith(PurchaseCallbackPrefix, StringComparison.Ordinal) ? EntryKind.Purchase : (EntryKind?)null;
            if (kind is null)
                return false;
            await HandleEntryCallbackAsync(bot, callback, callbackData, kind.Value, sessions, token);
            return true;
        }

        if ((update.Message ?? update.EditedMessage) is not { Text: { } input, From: { IsBot: false } sender } message ||
            !BotMenu.Supports(message.Chat.Type))
            return false;

        var text = input.Trim();
        var firstWord = BotMenu.FirstWord(text);
        if (BotMenu.IsAddressedElsewhere(firstWord, botUsername))
            return true;
        var command = BotMenu.CommandName(firstWord);
        if (command == "/" + BotMenu.Info)
        {
            if (update.Message is not null)
                await ReplyAsync(bot, message, BotMenu.Help(message.Chat.Type), token);
            return true;
        }

        var start = command is "/" + BotMenu.Purchase or "/testpurchase" ? EntryKind.Purchase
            : message.Chat.Type == ChatType.Private && command == "/" + BotMenu.Income &&
                firstWord.Length == text.Length ? EntryKind.Income : (EntryKind?)null;
        var cancel = command == "/" + BotMenu.Cancel;
        var conversation = new Conversation(message.Chat.Id, sender.Id, message.MessageThreadId);
        var draft = sessions.Find(conversation);
        var nextReply = draft is { Amount: null } && message.ReplyToMessage?.MessageId == draft.PromptId;
        if (start is null && !cancel && (!nextReply || command == "/" + BotMenu.Income))
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
            if (draft is null)
                await ReplyAsync(bot, message, "Нет активного ввода.", token);
            else
                await ReplyAsync(bot, message,
                    await CompleteEntryWizardAsync(bot, sessions, conversation, draft, CancelledText(draft), token), token);
            return true;
        }

        if (start is { } entryKind)
        {
            if (draft is not null)
                await CompleteEntryWizardAsync(bot, sessions, conversation, draft, CancelledText(draft), token);
            draft = sessions.Start(conversation, entryKind);
            await AskAsync(bot, message, draft, entryKind == EntryKind.Income ? "Источник дохода" : "Название покупки", token);
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
            await AskAsync(bot, message, draft, draft.Kind == EntryKind.Income ? "Сумма дохода" : "Сумма покупки", token);
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
        var currencyPrompt = await bot.SendMessage(message.Chat.Id, draft.Kind == EntryKind.Income ? "Валюта дохода" : "Валюта покупки",
            replyMarkup: new InlineKeyboardMarkup(currencyButtons), messageThreadId: message.MessageThreadId,
            cancellationToken: token);
        draft.PromptId = currencyPrompt.MessageId;
        draft.Amount = amount;
        return true;
    }

    private async Task HandleEntryCallbackAsync(ITelegramBotClient bot, CallbackQuery callback,
        string data, EntryKind kind, BudgetEntrySessions sessions, CancellationToken token)
    {
        var message = callback.Message;
        var conversation = new Conversation(message?.Chat.Id ?? 0, callback.From.Id, message?.MessageThreadId);
        var payload = data[CallbackPrefix(kind).Length..].Split(':', 2);
        if (message is null || !BotMenu.Supports(message.Chat.Type) || callback.From.IsBot ||
            (kind == EntryKind.Income && message.Chat.Type != ChatType.Private) ||
            payload is not [var id, var action] || !Guid.TryParseExact(id, "N", out var draftId) ||
            sessions.Find(conversation) is not { Name: { } name, Amount: { } amount } draft ||
            draft.Kind != kind || draft.Id != draftId || message.MessageId != draft.PromptId)
        {
            await bot.AnswerCallbackQuery(callback.Id, "Действие недоступно.", cancellationToken: token);
            return;
        }

        if (draft.Outcome is not null || action == "cancel")
        {
            var outcome = await CompleteEntryWizardAsync(bot, sessions, conversation, draft, CancelledText(draft), token);
            await bot.AnswerCallbackQuery(callback.Id, outcome, cancellationToken: token);
            return;
        }

        var currencies = settings.ManualCurrencies.Currencies;
        if (action == "save" && draft.Currency is { } code && currencies.Contains(code))
        {
            if (await usersService.GetUserByTelegramId(conversation.UserId, token) is not { } owner)
            {
                await bot.AnswerCallbackQuery(callback.Id, "Действие недоступно.", cancellationToken: token);
                return;
            }

            bool created;
            if (kind == EntryKind.Income)
            {
                // The bot's confirmation message has a stable ID within this private chat.
                var existing = await incomesService.GetEntityByMessageId(conversation.UserId, draft.PromptId, token);
                created = existing.Count == 0;
                if (created)
                    await incomesService.SaveMessageIncomes(conversation.UserId, draft.PromptId,
                        [new Income { Source = name, Sum = amount, Currency = code }], token);
            }
            else
            {
                created = await purchasesService.GetPurchaseById(draft.Id) is null;
                if (created)
                    await purchasesService.Add(new Purchase
                    {
                        Id = draft.Id, Name = name, Currency = code,
                        Items = new() { NewManualItem(name, amount) }
                    }, conversation.UserId, token);
            }
            var outcome = await CompleteEntryWizardAsync(bot, sessions, conversation, draft,
                kind == EntryKind.Income ? "Доход сохранён." : "Покупка сохранена.", token);
            await bot.AnswerCallbackQuery(callback.Id, outcome, cancellationToken: token);
            if (created)
                await NotifyUsersAsync(bot, kind == EntryKind.Income
                    ? $"{settings.TelegramBot.IncomeCreateEmoji} {owner.Name} добавил(а) доход [{name}] — {FormatManualAmount(amount, code)}"
                    : $"{settings.TelegramBot.PurchaseCreateEmoji} {owner.Name} добавил(а) покупку [{name}] — {FormatManualAmount(amount, code)}",
                    token, conversation.UserId);
            return;
        }

        if (draft.Currency is null && currencies.Contains(action))
        {
            try
            {
                await bot.EditMessageText(conversation.ChatId, draft.PromptId, $"{name}\n{FormatManualAmount(amount, action)}",
                    replyMarkup: new InlineKeyboardMarkup(new[] { Button("Сохранить", draft, "save"), Button("Отмена", draft, "cancel") }),
                    cancellationToken: token);
            }
            catch (ApiRequestException ex) when (MessageNotModified(ex))
            {
                // Telegram may have applied the edit before a previous request timed out.
            }
            draft.Currency = action;
            await bot.AnswerCallbackQuery(callback.Id, cancellationToken: token);
            return;
        }

        await bot.AnswerCallbackQuery(callback.Id, "Действие недоступно.", cancellationToken: token);
    }

    private static async Task<string> CompleteEntryWizardAsync(ITelegramBotClient bot, BudgetEntrySessions sessions,
        Conversation conversation, Draft draft, string outcome, CancellationToken token)
    {
        draft.Outcome ??= outcome;
        if (draft.Amount is not null)
        {
            try
            {
                await bot.EditMessageReplyMarkup(conversation.ChatId, draft.PromptId,
                    replyMarkup: new InlineKeyboardMarkup(Array.Empty<InlineKeyboardButton[]>()), cancellationToken: token);
            }
            catch (ApiRequestException ex) when (MessageNotModified(ex) ||
                ex.ErrorCode == 400 && ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase))
            {
                // The keyboard has already been removed.
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                CastarTelegramBotClient.LogError(ex);
                return draft.Outcome;
            }
        }

        sessions.Remove(conversation);
        return draft.Outcome;
    }

    private static bool MessageNotModified(ApiRequestException ex) =>
        ex.ErrorCode == 400 && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);

    private static async Task AskAsync(ITelegramBotClient bot, Message message,
        Draft draft, string prompt, CancellationToken token)
    {
        var sent = await bot.SendMessage(message.Chat.Id, prompt,
            replyParameters: new ReplyParameters { MessageId = message.MessageId },
            replyMarkup: new ForceReplyMarkup { Selective = true },
            messageThreadId: message.MessageThreadId, cancellationToken: token);
        draft.PromptId = sent.MessageId;
    }

    private static InlineKeyboardButton Button(string text, Draft draft, string action) =>
        InlineKeyboardButton.WithCallbackData(text, $"{CallbackPrefix(draft.Kind)}{draft.Id:N}:{action}");

    private static string CallbackPrefix(EntryKind kind) => kind == EntryKind.Income ? IncomeCallbackPrefix : PurchaseCallbackPrefix;

    private static string CancelledText(Draft draft) => draft.Kind == EntryKind.Income ? "Доход отменён." : "Покупка отменена.";
}
