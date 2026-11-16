using Castar.Api;
using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class ManualPurchaseEditor : UserControl
{
    private readonly PurchaseEditor header;
    private readonly string defaultCurrency;
    private readonly Guid purchaseId = Guid.NewGuid();

    internal ManualPurchaseEditor(DateTime localDate, ManualCurrencySettingsDto currencies, IReadOnlyList<User> owners)
    {
        defaultCurrency = currencies.DefaultCurrency;
        InitializeComponent();
        header = new PurchaseEditor(
            new(purchaseId, Guid.Empty, "", 0, defaultCurrency, 0, localDate, null, null, []),
            currencies.Currencies, owners);
        HeaderFields.Content = header;
        AddItem();
    }

    internal PurchaseCreateDto? Result { get; private set; }

    internal ValidatePopupDialog Validate()
    {
        var headerCheck = header.Validate();
        if (!headerCheck.Result)
            return headerCheck;
        if (ItemFields.Children.Count == 0)
            return new(false, "Добавьте хотя бы один товар.");

        var items = new List<PurchaseItemWriteDto>(ItemFields.Children.Count);
        foreach (DockPanel row in ItemFields.Children)
        {
            var editor = (PurchaseItemEditor)row.Children[1];
            var check = editor.Validate();
            if (!check.Result)
                return check;
            items.Add(editor.Result!);
        }
        var draft = header.Result!;
        Result = new(purchaseId, draft with { Currency = draft.Currency ?? defaultCurrency }, items.ToArray());
        return new(true, null);
    }

    private void AddItem_Click(object sender, RoutedEventArgs e) => AddItem();

    private void AddItem()
    {
        if (ItemFields.Children.Count >= 250)
            return;

        var row = new DockPanel();
        var remove = new Button
        {
            Content = FindResource("Svg.Remove"), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        AutomationProperties.SetName(remove, "Удалить товар");
        remove.Click += (_, _) => ItemFields.Children.Remove(row);
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);
        row.Children.Add(new PurchaseItemEditor(new(Guid.Empty, "", null, 1, 0, 0), null));
        ItemFields.Children.Add(row);
    }
}
