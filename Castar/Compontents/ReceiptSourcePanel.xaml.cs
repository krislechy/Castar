using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXingCpp;

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

    private void ShowQr_Click(object sender, RoutedEventArgs e)
    {
        if (QrPanel.Visibility == Visibility.Visible)
        {
            QrPanel.Visibility = Visibility.Collapsed;
            QrButton.Content = "Показать как QR-код";
            return;
        }

        try
        {
            QrImage.Source ??= CreateQr(source);
            QrPanel.Visibility = Visibility.Visible;
            QrButton.Content = "Скрыть QR-код";
        }
        // ZXingCpp 0.5.3 throws System.Exception for native encoder errors.
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException
            || error.GetType() == typeof(Exception))
        {
            QrError.Visibility = Visibility.Visible;
        }
    }

    internal static BitmapSource CreateQr(string value)
    {
        using var creator = new BarcodeCreator(BarcodeFormat.QRCode) { Options = """{"eci":"26"}""" };
        // The string overload marshals through the Windows code page. ECI 26 marks these bytes as UTF-8.
        using var barcode = creator.From(Encoding.UTF8.GetBytes(value));
        using var options = new WriterOptions { Scale = 4, AddQuietZones = true };
        using var pixels = barcode.ToImage(options);
        var bitmap = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Gray8,
            null, pixels.ToArray(), pixels.Width);
        bitmap.Freeze();
        return bitmap;
    }
}
