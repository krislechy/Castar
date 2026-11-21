using Castar.Domain.Models.DataBase;

namespace Castar.DAL.Repositories.DataBase
{
    public delegate void IncomeWriteCommitted(IReadOnlyList<Income> created);

    public interface IIncomesRepository : IBaseRepository<Income>
    {
        Task<IReadOnlyList<Income>> List(DateTime start, DateTime finish, bool includeUser);
        Task<IReadOnlyList<Income>> ListByMessage(Guid userId, long messageId, CancellationToken cancellationToken);
        Task Update(Income income, bool updateCategory, CancellationToken cancellationToken = default);
        Task SaveMessageIncomes(Guid userId, long messageId, IReadOnlyList<Income> incomes, CancellationToken cancellationToken);
        Task<int> CountUncategorizedAsync(DateTime? first, DateTime? endExclusive, CancellationToken token);
        Task<int> CountPendingClassificationAsync(DateTime? first, DateTime? endExclusive, CancellationToken token);
        Task<(int QueuedCount, int PendingCount)> QueueUncategorizedAsync(DateTime? first, DateTime? endExclusive,
            CancellationToken token);
        Task<IReadOnlyList<PendingIncomeClassification>> GetPendingClassificationAsync(int limit,
            DateTime? first, DateTime? endExclusive, CancellationToken token);
        Task<IncomeClassificationResult> ApplyClassificationAsync(Guid id, long revision, Guid? categoryId,
            CancellationToken token);
    }

    public sealed record PendingIncomeClassification(Guid IncomeId, long Revision, string Source,
        bool HasHistory, Guid? CategoryId);

    public enum IncomeClassificationResult { Applied, NotFound, Conflict, Unchanged }
}
