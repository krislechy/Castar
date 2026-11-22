using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class UncategorizedStatisticsContent : UserControl
{
    public event RoutedEventHandler? PurchasesRequested;
    public event RoutedEventHandler? IncomesRequested;
    public UncategorizedStatisticsContent() => InitializeComponent();
    private void Purchases_Click(object sender, RoutedEventArgs e) => PurchasesRequested?.Invoke(this, e);
    private void Incomes_Click(object sender, RoutedEventArgs e) => IncomesRequested?.Invoke(this, e);
}