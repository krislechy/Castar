using Castar.Api;
using Castar.Currency;
using System.Globalization;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class PurchaseItemEditor : UserControl
{
    private readonly string originalName;

    internal PurchaseItemEditor(PurchaseItemDto item, string? currency)
    {
        originalName = item.Name;
        InitializeComponent();
        ItemName.Text = item.Name;
        Quantity.Text = item.Quantity.ToString(CultureInfo.CurrentCulture);
        UnitPrice.Text = item.Price.ToString(CultureInfo.CurrentCulture);
        if (HistoricalExchangeRates.IsCurrencyCode(currency))
            PriceLabel.Text = $"Цена за единицу ({currency})";
    }

    internal PurchaseItemWriteDto? Result { get; private set; }

    internal ValidatePopupDialog Validate()
    {
        var name = ItemName.Text == originalName ? originalName : ItemName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 400)
            return new(false, "Укажите название позиции длиной до 400 символов.");
        if (!double.TryParse(Quantity.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var quantity)
            || !double.IsFinite(quantity) || double.Abs(quantity) >= 1_000_000 || (decimal)quantity == 0)
            return new(false, "Укажите ненулевое количество с модулем меньше 1 000 000.");
        const NumberStyles priceStyle = NumberStyles.Number & ~(NumberStyles.AllowThousands | NumberStyles.AllowTrailingSign);
        if (!decimal.TryParse(UnitPrice.Text, priceStyle, CultureInfo.CurrentCulture, out var price)
            || decimal.Abs(price) >= 10_000_000_000_000_000_000_000m)
            return new(false, "Укажите цену в допустимом диапазоне.");

        Result = new(name, quantity, price);
        return new(true, null);
    }
}
