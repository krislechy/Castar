using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Castar.Services.TelegramBot.Commands;

internal static class BotMenu
{
    public const string Purchase = "purchase";
    public const string Cancel = "cancel";
    public const string Info = "info";
    public const string Income = "income";
    public const string Me = "me";

    private static readonly BotCommand PurchaseCommand = new() { Command = Purchase, Description = "Добавить покупку" };
    private static readonly BotCommand CancelCommand = new() { Command = Cancel, Description = "Отменить ввод" };
    private static readonly BotCommand InfoCommand = new() { Command = Info, Description = "Команды и форматы ввода" };

    private static readonly BotCommand[] CommonCommands =
    [
        PurchaseCommand, CancelCommand, InfoCommand
    ];

    private static readonly BotCommand[] PrivateCommands =
    [
        PurchaseCommand,
        new() { Command = Income, Description = "Добавить доход" },
        CancelCommand,
        InfoCommand,
        new() { Command = Me, Description = "Показать мой Telegram ID" }
    ];

    internal static async Task<(string? Username, bool Configured)> ConfigureAsync(ITelegramBotClient bot, CancellationToken token)
    {
        string? username = null;
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            request.CancelAfter(TimeSpan.FromSeconds(5));
            username = (await bot.GetMe(request.Token)).Username;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            CastarTelegramBotClient.LogError(ex);
        }

        var privateMenu = await SetCommandsAsync(bot, PrivateCommands, new BotCommandScopeAllPrivateChats(), token);
        var groupMenu = await SetCommandsAsync(bot, CommonCommands, new BotCommandScopeAllGroupChats(), token);
        return (username, username is not null && privateMenu && groupMenu);
    }

    private static async Task<bool> SetCommandsAsync(ITelegramBotClient bot, BotCommand[] commands, BotCommandScope scope,
        CancellationToken token)
    {
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
            request.CancelAfter(TimeSpan.FromSeconds(5));
            await bot.SetMyCommands(commands, scope: scope, cancellationToken: request.Token);
            return true;
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            CastarTelegramBotClient.LogError(ex);
            return false;
        }
    }

    internal static bool Supports(ChatType chat) => chat is ChatType.Private or ChatType.Group or ChatType.Supergroup;

    internal static string FirstWord(string? text) => text?.TrimStart()
        .Split([' ', '\t', '\r', '\n'], 2)[0] ?? "";

    internal static bool IsAddressedElsewhere(string firstWord, string? username)
    {
        var separator = firstWord.IndexOf('@');
        return firstWord.StartsWith('/') && separator >= 0 &&
            !firstWord[(separator + 1)..].Equals(username, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CommandName(string firstWord) => firstWord.Split('@', 2)[0].ToLowerInvariant();

    internal static string NormalizePrivateCommand(string text, string? username)
    {
        var firstWord = FirstWord(text);
        var separator = firstWord.IndexOf('@');
        if (separator < 0 || IsAddressedElsewhere(firstWord, username) ||
            CommandName(firstWord) is not ("/" + Income or "/" + Me))
            return text;

        return text.Remove(separator, firstWord.Length - separator);
    }

    internal static string Help(ChatType chat)
    {
        var commands = chat == ChatType.Private ? PrivateCommands : CommonCommands;
        var commandList = string.Join('\n', commands.Select(command => $"/{command.Command} — {command.Description}"));
        return commandList + "\n\n" + (chat == ChatType.Private ? PrivateHelp : "Чеки, доходы и текстовые покупки доступны в личном чате с ботом.");
    }

    private static readonly string PrivateHelp = """
        Покупка текстом: Хлеб 150 RSD. Для нескольких товаров используйте отдельные строки с одной валютой.
        Доход пошагово: /income → источник → сумма → валюта → сохранить.
        Доход текстом: /income Оплата за проект 100 EUR. Для нескольких доходов повторите /income в каждой строке.
        Код валюты можно опустить, тогда применяется валюта ручного добавления.
        Чек: отправьте фото, файл с QR-кодом или ссылку из него.
        При сбое поставщика бот повторит загрузку и обновит статус чека.

        Изменение: отредактируйте исходное текстовое сообщение.
        Удаление покупки: укажите отрицательную сумму в одной строке, сохранив число строк.
        Удаление дохода: укажите отрицательную сумму в нужной строке.
        Чеки и записи, добавленные пошагово, редактируются и удаляются в Castar.
        """;
}
