using Castar.Api;
using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Castar.Compontents;

public partial class ManualPurchaseEditor : UserControl
{
    private readonly PurchaseEditor header;
    private readonly string defaultCurrency;
    private readonly IReadOnlyList<BudgetCategoryDto>? categories;
    private readonly Guid purchaseId = Guid.NewGuid();

    internal ManualPurchaseEditor(DateTime localDate, ManualCurrencySettingsDto currencies, IReadOnlyList<User> owners,
        IReadOnlyList<BudgetCategoryDto>? categories = null)
    {
        defaultCurrency = currencies.DefaultCurrency;
        this.categories = categories;
        InitializeComponent();
        header = new PurchaseEditor(
            new(purchaseId, Guid.Empty, "", 0, defaultCurrency, 0, localDate, null, null, []),
            currencies.Currencies, owners, manualEntry: true);
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

    private void AddItem_Click(object sender, RoutedEventArgs e) => AddItem()?.FocusItemName();

    private PurchaseItemEditor? AddItem()
    {
        if (ItemFields.Children.Count >= 250)
            return null;

        var row = new DockPanel();
        var editor = new PurchaseItemEditor(new(Guid.Empty, "", null, 1, 0, 0), null, manualEntry: true, categories: categories);
        editor.ShowHeadings(ItemFields.Children.Count == 0);
        var icon = new Path
        {
            Data = (Geometry)FindResource("Lucide.x"),
            Width = 12, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        };
        icon.SetResourceReference(Shape.StrokeProperty, "PurchaseForm.Muted");
        var remove = new Button
        {
            Content = icon, Width = 26, Height = 26, Padding = new Thickness(4),
            Margin = new Thickness(8, 0, 0, 8), IsTabStop = false, VerticalAlignment = VerticalAlignment.Bottom
        };
        remove.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
        remove.SetResourceReference(BackgroundProperty, "PurchaseForm.RemoveSurface");
        AutomationProperties.SetName(remove, "Удалить товар");
        remove.Click += (_, _) =>
        {
            ItemFields.Children.Remove(row);
            if (ItemFields.Children.Count > 0 && ItemFields.Children[0] is DockPanel first)
                ((PurchaseItemEditor)first.Children[1]).ShowHeadings(true);
        };
        DockPanel.SetDock(remove, Dock.Right);
        row.Children.Add(remove);
        row.Children.Add(editor);
        ItemFields.Children.Add(row);
        return editor;
    }
}
