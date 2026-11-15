using System.Drawing;

namespace Castar.Services.Receipts.Barcode;

public interface IQrCode
{
    string? Decode(Bitmap bitmap);
}
