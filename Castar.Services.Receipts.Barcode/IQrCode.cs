namespace Castar.Services.Receipts.Barcode;

public interface IQrCode
{
    string? Decode(Stream image, CancellationToken cancellationToken = default);
}
