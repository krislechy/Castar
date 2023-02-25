using System.Drawing;
using ZXing;
using ZXing.Common;

namespace Castar.Services.Receipts.Barcode
{
    public class QrCode:IQrCode
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Проверка совместимости платформы", Justification = "<Ожидание>")]
        public string Decode(System.Drawing.Bitmap bitmap)
        {
            try
            {
                var barcodeReader = new BarcodeReader<Bitmap>(e => new BitmapLuminanceSource(bitmap))
                {
                    AutoRotate = true,
                    Options = new DecodingOptions
                    {
                        TryHarder = true,
                        TryInverted = true,
                        PossibleFormats = new List<ZXing.BarcodeFormat>()
                    {
                        ZXing.BarcodeFormat.QR_CODE
                    },
                        PureBarcode = false,
                        ReturnCodabarStartEnd = false,
                    },
                };
                var res = barcodeReader.Decode(bitmap);
                if (res != null)
                {
                    return res.Text;
                }
                else throw new Exception("QR-code не удалось прочитать");

            }
            catch (Exception ex){
                throw;
            }
        }
    }
}