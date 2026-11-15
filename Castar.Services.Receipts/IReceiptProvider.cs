namespace Castar.Services.Receipts;

public interface IReceiptProvider
{
    string Id { get; }

    bool CanHandle(string query);

    Task<Receipt> GetReceiptAsync(string query, CancellationToken cancellationToken);
}
