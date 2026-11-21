using Castar.Domain.Models.DataBase;

namespace Castar.DAL.Repositories.DataBase
{
    public interface IIncomesRepository : IBaseRepository<Income>
    {
        Task<IReadOnlyList<Income>> List(DateTime start, DateTime finish, bool includeUser);
        Task<IReadOnlyList<Income>> ListByMessage(Guid userId, long messageId, CancellationToken cancellationToken);
        Task Update(Income income, bool updateCategory, CancellationToken cancellationToken = default);
        Task SaveMessageIncomes(Guid userId, long messageId, IReadOnlyList<Income> incomes, CancellationToken cancellationToken);
    }
}
