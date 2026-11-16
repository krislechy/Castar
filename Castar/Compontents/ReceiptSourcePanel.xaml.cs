using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace Castar.Compontents;

public partial class ReceiptSourcePanel : UserControl
{
    private readonly string source;

    internal ReceiptSourcePanel(string source, bool isLegacy)
    {
        this.source = source;
        InitializeComponent();
        SourceText.Text = source;
        LegacyStatus.Visibility = isLegacy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(source);
            CopyError.Visibility = Visibility.Collapsed;
        }
        catch (ExternalException)
        {
            CopyError.Visibility = Visibility.Visible;
        }
    }
}
