using System.Drawing;
using ZXing;
using ZXing.Common;

namespace Castar.Services.Receipts.Barcode;

public sealed class QrCode : IQrCode
{
    public string? Decode(Bitmap bitmap)
    {
        var reader = new BarcodeReader<Bitmap>(image => new BitmapLuminanceSource(image))
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = [BarcodeFormat.QR_CODE]
            }
        };

        return reader.Decode(bitmap)?.Text;
    }
}
