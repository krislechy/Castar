using Castar.Currency;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public sealed record MoneyAmountDisplay(string Primary, string? Original)
{
    internal static MoneyAmountDisplay Create(decimal amount, string? currency, decimal? converted = null, string? target = null)
    {
        var original = HistoricalExchangeRates.Format(amount, currency);
        return converted is { } value && currency != target && HistoricalExchangeRates.IsCurrencyCode(currency) &&
            HistoricalExchangeRates.IsCurrencyCode(target)
            ? new(HistoricalExchangeRates.Format(value, target), original)
            : new(original, null);
    }
}

public partial class MoneyAmount : UserControl
{
    public static readonly DependencyProperty PrefixProperty =
        DependencyProperty.Register(nameof(Prefix), typeof(string), typeof(MoneyAmount), new PropertyMetadata(""));

    public string Prefix
    {
        get => (string)GetValue(PrefixProperty);
        set => SetValue(PrefixProperty, value);
    }

    public static readonly DependencyProperty DisplayProperty =
        DependencyProperty.Register(nameof(Display), typeof(MoneyAmountDisplay), typeof(MoneyAmount));

    public MoneyAmountDisplay? Display
    {
        get => (MoneyAmountDisplay?)GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    public static readonly DependencyProperty AmountAlignmentProperty =
        DependencyProperty.Register(nameof(AmountAlignment), typeof(TextAlignment), typeof(MoneyAmount),
            new PropertyMetadata(TextAlignment.Right));

    public TextAlignment AmountAlignment
    {
        get => (TextAlignment)GetValue(AmountAlignmentProperty);
        set => SetValue(AmountAlignmentProperty, value);
    }

    public MoneyAmount() => InitializeComponent();
}
