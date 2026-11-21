using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public sealed class PeriodClassificationButton : Button
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(int), typeof(PeriodClassificationButton), new PropertyMetadata(0));

    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(
        nameof(IsBusy), typeof(bool), typeof(PeriodClassificationButton), new PropertyMetadata(false));

    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(
        nameof(IsAvailable), typeof(bool), typeof(PeriodClassificationButton), new PropertyMetadata(false));

    public PeriodClassificationButton() => SetResourceReference(StyleProperty, "LedgerPeriodClassificationButton");

    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public bool IsBusy
    {
        get => (bool)GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    public bool IsAvailable
    {
        get => (bool)GetValue(IsAvailableProperty);
        set => SetValue(IsAvailableProperty, value);
    }
}
