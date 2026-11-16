using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Castar.Compontents;

public sealed class DecimalInput : TextBox
{
    public static readonly DependencyProperty FractionDigitsProperty = DependencyProperty.Register(nameof(FractionDigits),
        typeof(int), typeof(DecimalInput), new FrameworkPropertyMetadata(2), value => (int)value >= -1);

    public static readonly DependencyProperty AllowNegativeProperty = DependencyProperty.Register(nameof(AllowNegative),
        typeof(bool), typeof(DecimalInput), new FrameworkPropertyMetadata(true));

    public int FractionDigits { get => (int)GetValue(FractionDigitsProperty); set => SetValue(FractionDigitsProperty, value); }
    public bool AllowNegative { get => (bool)GetValue(AllowNegativeProperty); set => SetValue(AllowNegativeProperty, value); }

    public DecimalInput()
    {
        SetResourceReference(StyleProperty, typeof(TextBox));
        DataObject.AddPastingHandler(this, Input_Pasting);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (e.Handled || e.Text.Length == 0)
            return;

        var inserted = Normalize(e.Text);
        if (!IsValidEdit(inserted))
            e.Handled = true;
        else if (inserted != e.Text)
        {
            ReplaceSelection(inserted);
            e.Handled = true;
        }
    }

    private void Input_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || text.Length > 128)
        {
            e.CancelCommand();
            return;
        }

        var inserted = Normalize(text.Trim());
        if (IsValidEdit(inserted))
        {
            e.DataObject = new DataObject(DataFormats.UnicodeText, inserted);
            e.FormatToApply = DataFormats.UnicodeText;
        }
        else
            e.CancelCommand();
    }

    private void ReplaceSelection(string inserted)
    {
        var start = SelectionStart;
        SelectedText = inserted;
        CaretIndex = start + inserted.Length;
    }

    private bool IsValidEdit(string inserted)
    {
        var proposed = Text.Remove(SelectionStart, SelectionLength).Insert(SelectionStart, inserted);
        var separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var negative = CultureInfo.CurrentCulture.NumberFormat.NegativeSign;
        var digits = AllowNegative && proposed.StartsWith(negative, StringComparison.Ordinal)
            ? proposed.AsSpan(negative.Length) : proposed.AsSpan();
        var point = digits.IndexOf(separator.AsSpan());
        if (point < 0)
            return IsAsciiDigits(digits);

        var fraction = digits[(point + separator.Length)..];
        return FractionDigits != 0 && (FractionDigits < 0 || fraction.Length <= FractionDigits)
            && IsAsciiDigits(digits[..point]) && IsAsciiDigits(fraction);
    }

    private static string Normalize(string text)
    {
        var format = CultureInfo.CurrentCulture.NumberFormat;
        return text.Replace(".", format.NumberDecimalSeparator).Replace(",", format.NumberDecimalSeparator)
            .Replace("−", format.NegativeSign);
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
            if (character is < '0' or > '9')
                return false;
        return true;
    }
}
