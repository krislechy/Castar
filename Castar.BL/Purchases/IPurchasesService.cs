using System.Collections.ObjectModel;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public interface IPurchasesService
    {
        Task<ObservableCollection<Purchase>> GetEntities(DateTime start, DateTime finish);
        Task<ObservableCollection<Purchase>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish);
        Task<Purchase?> GetPurchaseById(Guid id);
        Task<bool> IsExistTheSame(byte[]? query);
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
