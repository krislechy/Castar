using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public interface IPurchasesService
    {
        Task<IReadOnlyList<Purchase>> ListAsync(DateTime start, DateTime finish, CancellationToken cancellationToken);
        Task<Purchase?> GetPurchaseById(Guid id);
        Task<Purchase> Add(Purchase purchase, long telegramUserId, CancellationToken cancellationToken = default);
        Task<Purchase?> AddReceipt(Purchase purchase, long telegramUserId, string provider, string receiptId,
            CancellationToken cancellationToken = default);
        Task<Purchase?> GetManualPurchaseByMessageId(
            long telegramUserId, long messageId, CancellationToken cancellationToken = default);
        Task Update(Purchase purchase, CancellationToken cancellationToken = default);
        Task Delete(Guid id, CancellationToken cancellationToken = default);
        Task<PurchaseMutationResult> DeletePurchaseAsync(Guid purchaseId, long expectedRevision, CancellationToken cancellationToken = default);
    }
}
