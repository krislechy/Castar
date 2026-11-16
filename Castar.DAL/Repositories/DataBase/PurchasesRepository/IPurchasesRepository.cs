using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;

namespace Castar.DAL.Repositories.DataBase
{
    public interface IPurchasesRepository : IBaseRepository<Purchase>
    {
        Task<IReadOnlyList<Purchase>> List(DateTime start, DateTime finish, bool includeDetails);
        Task<Purchase?> FindManualByMessage(Guid userId, long messageId, CancellationToken cancellationToken);
        Task<Purchase?> AddReceipt(Purchase purchase, string provider, string receiptId, CancellationToken cancellationToken);
        Task<bool> SearchByRawData(byte[]? query);
        Task<(bool Found, string? ReceiptSource, bool IsLegacy)> GetReceiptSourceAsync(
            Guid purchaseId, CancellationToken cancellationToken = default);
        Task<PurchaseMutationResult> DeletePurchaseAsync(
            Guid purchaseId, long expectedRevision, CancellationToken cancellationToken = default);
        Task<PurchaseMutationResult> DeletePurchaseItemAsync(
            Guid purchaseId, Guid itemId, long expectedRevision, CancellationToken cancellationToken = default);
        Task<PurchaseMutationResult> EditPurchaseAsync(
            Guid purchaseId, long expectedRevision, string name, string? retailPlace, string? retailPlaceAddress,
            DateTime addedOn, string? currency = null, ManualCurrencySettings? currencyPolicy = null,
            CancellationToken cancellationToken = default);
        Task<PurchaseMutationResult> EditPurchaseItemAsync(
            Guid purchaseId, Guid itemId, long expectedRevision, string name, double quantity, decimal price,
            CancellationToken cancellationToken = default);
    }

    public enum PurchaseMutationResult
    {
        Applied,
        NotFound,
        Conflict,
        InvalidCurrency
    }
}
