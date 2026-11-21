using Castar.Api;
using Castar.Currency;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Castar.Compontents;

public partial class PurchaseItemEditor : UserControl
{
    private readonly string originalName;
    private readonly Guid? originalCategoryId;
    private readonly bool confirmExistingCategory;

    internal PurchaseItemEditor(PurchaseItemDto item, string? currency, bool manualEntry = false, bool usePurchaseFormStyle = false,
        IReadOnlyList<BudgetCategoryDto>? categories = null)
    {
        originalName = item.Name;
        originalCategoryId = item.BudgetCategoryId;
        confirmExistingCategory = item.Id != Guid.Empty && originalCategoryId.HasValue && !item.CategoryConfirmed;
        IsManualEntry = manualEntry;
        UsesPurchaseFormStyle = manualEntry || usePurchaseFormStyle;
        InitializeComponent();
        ItemName.Text = item.Name;
        Quantity.Text = item.Quantity.ToString(CultureInfo.CurrentCulture);
        UnitPrice.Text = item.Price.ToString(CultureInfo.CurrentCulture);
        ItemCategory.Load(categories, originalCategoryId, compact: manualEntry);
        if (HistoricalExchangeRates.IsCurrencyCode(currency))
            PriceLabel.Text = $"Цена ({currency})";
        if (manualEntry)
        {
            Grid.SetRow(ItemCategory, 0);
            Grid.SetColumn(ItemCategory, 3);
            Grid.SetColumnSpan(ItemCategory, 1);
            ItemCategory.Margin = new Thickness(8, 0, 0, 0);
            ItemCategory.VerticalAlignment = VerticalAlignment.Bottom;
            Quantity.Background = Brushes.Transparent;
            Quantity.BorderThickness = new Thickness(0);
            Quantity.Padding = new Thickness(3, 7, 3, 7);
            Quantity.HorizontalContentAlignment = HorizontalAlignment.Center;
            Quantity.TextChanged += (_, _) => UpdateStepper();
            UpdateStepper();
        }
    }

    public bool IsManualEntry { get; }
    public bool UsesPurchaseFormStyle { get; }
    internal void FocusItemName() => ItemName.Focus();

    internal void ShowHeadings(bool show)
    {
        NameLabel.Visibility = QuantityLabel.Visibility = PriceLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ItemName, NameLabel.Text);
        AutomationProperties.SetName(Quantity, QuantityLabel.Text);
        AutomationProperties.SetName(UnitPrice, PriceLabel.Text);
    }

    private void DecreaseQuantity_Click(object sender, RoutedEventArgs e) => ChangeQuantity(-1);
    private void IncreaseQuantity_Click(object sender, RoutedEventArgs e) => ChangeQuantity(1);

    private void ChangeQuantity(int step)
    {
        if (TryStepQuantity(step, out var next))
            Quantity.Text = next.ToString(CultureInfo.CurrentCulture);
    }

    private void UpdateStepper()
    {
        DecreaseQuantityButton.IsEnabled = TryStepQuantity(-1, out _);
        IncreaseQuantityButton.IsEnabled = TryStepQuantity(1, out _);
    }

    private bool TryStepQuantity(int step, out decimal next)
    {
        next = step;
        if (string.IsNullOrWhiteSpace(Quantity.Text))
            return true;
        if (!decimal.TryParse(Quantity.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            || value <= -1_000_000 || value >= 1_000_000)
            return false;

        next = value + step;
        return next is > -1_000_000 and < 1_000_000 and not 0;
    }

    internal PurchaseItemWriteDto? Result { get; private set; }

    internal ValidatePopupDialog Validate()
    {
        var name = ItemName.Text == originalName ? originalName : ItemName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 400)
            return new(false, "Укажите название товара длиной до 400 символов.");
        if (!double.TryParse(Quantity.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var quantity)
            || !double.IsFinite(quantity) || double.Abs(quantity) >= 1_000_000 || (decimal)quantity == 0)
            return new(false, "Укажите ненулевое количество с модулем меньше 1 000 000.");
        const NumberStyles priceStyle = NumberStyles.Number & ~(NumberStyles.AllowThousands | NumberStyles.AllowTrailingSign);
        if (!decimal.TryParse(UnitPrice.Text, priceStyle, CultureInfo.CurrentCulture, out var price)
            || decimal.Abs(price) >= 10_000_000_000_000_000_000_000m)
            return new(false, "Укажите цену в допустимом диапазоне.");

        Result = new(name, quantity, price)
        {
            Category = ItemCategory.GetChange(originalCategoryId, confirmExistingCategory)
        };
        return new(true, null);
    }
}
