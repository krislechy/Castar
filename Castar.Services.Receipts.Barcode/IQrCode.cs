using System.Drawing;
using ZXing;
using ZXing.Common;

namespace Castar.Services.Receipts.Barcode
{
    public interface IQrCode
    {
        string Decode(System.Drawing.Bitmap bitmap);
    }
}