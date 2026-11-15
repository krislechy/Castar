using Castar.Domain.Models.DataBase;

namespace Castar.DAL.Repositories.DataBase
{
    public interface IPurchasesRepository : IBaseRepository<Purchase>
    {
        Task<IReadOnlyList<Purchase>> List(DateTime start, DateTime finish, bool includeDetails);
        Task<Purchase?> FindManualByMessage(Guid userId, long messageId, CancellationToken cancellationToken);
        Task<Purchase?> AddReceipt(Purchase purchase, string provider, string receiptId, CancellationToken cancellationToken);
        Task<bool> SearchByRawData(byte[]? query);
    }
}
