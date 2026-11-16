using Castar.Api;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Castar.Compontents;

internal sealed class TelegramPreferencesEditor : StackPanel
{
    private readonly TextBox incomeCreate;
    private readonly TextBox incomeUpdate;
    private readonly TextBox incomeDelete;
    private readonly TextBox purchaseCreate;
    private readonly TextBox purchaseUpdate;
    private readonly TextBox purchaseDelete;
    private readonly TextBox item;
    private readonly TextBox chat;

    internal TelegramPreferencesEditor(TelegramPreferencesDto preferences)
    {
        MinWidth = 300;
        var table = new Grid();
        table.ColumnDefinitions.Add(new());
        table.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        table.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        for (var row = 0; row < 5; row++)
            table.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Children.Add(table);
        Add(table, Label("Доход"), 0, 1);
        Add(table, Label("Расход"), 0, 2);
        Add(table, Label("Добавление"), 1, 0);
        Add(table, Label("Изменение"), 2, 0);
        Add(table, Label("Удаление"), 3, 0);
        Add(table, Label("Товар"), 4, 0);
        incomeCreate = Emoji(table, preferences.IncomeCreateEmoji, "Добавление дохода", 1, 1);
        incomeUpdate = Emoji(table, preferences.IncomeUpdateEmoji, "Изменение дохода", 2, 1);
        incomeDelete = Emoji(table, preferences.IncomeDeleteEmoji, "Удаление дохода", 3, 1);
        purchaseCreate = Emoji(table, preferences.PurchaseCreateEmoji, "Добавление расхода", 1, 2);
        purchaseUpdate = Emoji(table, preferences.PurchaseUpdateEmoji, "Изменение расхода", 2, 2);
        purchaseDelete = Emoji(table, preferences.PurchaseDeleteEmoji, "Удаление расхода", 3, 2);
        item = Emoji(table, preferences.ItemEmoji, "Товар", 4, 2);

        var chatLabel = Label("ID тестового чата");
        Children.Add(chatLabel);
        chat = new TextBox { Text = preferences.TestChatId?.ToString(CultureInfo.InvariantCulture) ?? "", MaxLength = 18 };
        AutomationProperties.SetLabeledBy(chat, chatLabel);
        Children.Add(chat);
    }

    internal TelegramPreferencesDto? Result { get; private set; }

    internal ValidatePopupDialog Validate()
    {
        long? chatId = null;
        if (!string.IsNullOrWhiteSpace(chat.Text))
        {
            if (!long.TryParse(chat.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
                !TelegramPreferencesDto.IsValidChatId(parsed))
                return new(false, "Проверьте ID тестового чата.");
            chatId = parsed;
        }
        var requested = new TelegramPreferencesDto(incomeCreate.Text.Trim(), incomeUpdate.Text.Trim(), incomeDelete.Text.Trim(),
            purchaseCreate.Text.Trim(), purchaseUpdate.Text.Trim(), purchaseDelete.Text.Trim(), item.Text.Trim(), chatId);
        if (!requested.IsValid())
            return new(false, "Укажите по одному значку в поле.");
        Result = requested;
        return new(true, null);
    }

    private static TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(StyleProperty, "FormFieldLabel");
        return label;
    }

    private static TextBox Emoji(Grid table, string value, string name, int row, int column)
    {
        var input = new TextBox
        {
            Text = value,
            TextAlignment = TextAlignment.Center,
            MaxLength = 32,
            Width = 65,
            Margin = new Thickness(4, 0, 0, 4)
        };
        AutomationProperties.SetName(input, name);
        Add(table, input, row, column);
        return input;
    }

    private static void Add(Grid table, UIElement child, int row, int column)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        table.Children.Add(child);
    }
}
