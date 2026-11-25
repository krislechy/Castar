using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;

namespace Castar.DAL.Repositories.DataBase
{
    public interface IPurchasesRepository : IBaseRepository<Purchase>
    {
        Task<IReadOnlyList<Purchase>> ListAsync(DateTime start, DateTime finish, CancellationToken cancellationToken);
        Task<Purchase?> FindManualByMessage(Guid userId, long messageId, CancellationToken cancellationToken);
        Task<Purchase?> FindWithItemsAsync(Guid id, CancellationToken cancellationToken);
        Task<Purchase?> AddReceipt(Purchase purchase, string provider, string receiptId, CancellationToken cancellationToken);
        Task<(bool Found, string? ReceiptSource, bool IsLegacy)> GetReceiptSourceAsync(
            Guid purchaseId, CancellationToken cancellationToken = default);
        Task<PurchaseMutationResult> DeletePurchaseAsync(
            Guid purchaseId, long expectedRevision, CancellationToken cancellationToken = default,
            bool supportsItemCurrency = true);
        Task<PurchaseMutationResult> DeletePurchaseItemAsync(
            Guid purchaseId, Guid itemId, long expectedRevision, CancellationToken cancellationToken = default,
            bool supportsItemCurrency = true);
        Task<PurchaseMutationResult> EditPurchaseAsync(
            Guid purchaseId, long expectedRevision, string name, string? retailPlace, string? retailPlaceAddress,
            DateTime addedOn, string? currency = null, ManualCurrencySettings? currencyPolicy = null,
            CancellationToken cancellationToken = default, Guid? userId = null, DateTime? legacyDate = null,
            bool supportsItemCurrency = true);
        Task<PurchaseMutationResult> EditPurchaseItemAsync(
            Guid purchaseId, Guid itemId, long expectedRevision, string name, double quantity, decimal price,
            CancellationToken cancellationToken = default, Guid? categoryId = null, bool updateCategory = false,
            string? currency = null, ManualCurrencySettings? currencyPolicy = null, bool supportsItemCurrency = true);
        Task<PurchaseMutationResult> AddPurchaseItemAsync(
            Guid purchaseId, long expectedRevision, string name, double quantity, decimal price,
            CancellationToken cancellationToken = default, Guid? categoryId = null, bool updateCategory = false,
            string? currency = null, ManualCurrencySettings? currencyPolicy = null, bool supportsItemCurrency = true);
        Task<IReadOnlyList<PendingPurchaseClassification>> GetPendingClassificationAsync(
            int limit, CancellationToken cancellationToken, DateTime? first = null, DateTime? endExclusive = null);
        Task<(int QueuedCount, int PendingCount)> QueueUncategorizedItemsAsync(CancellationToken cancellationToken,
            DateTime? first = null, DateTime? endExclusive = null);
        Task<int> CountUncategorizedPurchasesAsync(DateTime? first, DateTime? endExclusive, CancellationToken token);
        Task<int> CountPendingItemsAsync(DateTime? first, DateTime? endExclusive, CancellationToken token);
        Task<PurchaseMutationResult> ApplyClassificationAsync(Guid purchaseId, long expectedRevision,
            IReadOnlyDictionary<Guid, Guid?> results, CancellationToken cancellationToken, bool reclassify = false);
    }

    public enum PurchaseMutationResult
    {
        Applied,
        NotFound,
        Conflict,
        InvalidCurrency,
        FiscalCurrency,
        FiscalItemAddition,
        FiscalItemDeletion,
        FiscalItemAmounts,
        ItemCurrencyInUse,
        UnsupportedItemCurrency,
        InvalidOwner,
        Unchanged
    }

    public sealed record PendingPurchaseClassification(Guid PurchaseId, long Revision, string Merchant,
        IReadOnlyList<PendingItemClassification> Items);

    public sealed record PendingItemClassification(Guid ItemId, string Name, bool HasHistory, Guid? CategoryId);
}
