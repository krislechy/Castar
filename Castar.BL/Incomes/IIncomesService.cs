using System.Collections.ObjectModel;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public interface IIncomesService
    {
        Task<ObservableCollection<Income>> GetEntities(DateTime start, DateTime finish);
        Task<ObservableCollection<Income>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish);
        Task<Income> Add(Income income, long telegramUserId, User? user = null);
        Task<bool> Delete(Guid id);
        Task Update(Income income, bool updateCategory = false);
        Task<IReadOnlyList<Income>> GetEntityByMessageId(
            long telegramUserId, long messageId, CancellationToken cancellationToken = default);
        Task SaveMessageIncomes(long telegramUserId, long messageId, IReadOnlyList<Income> incomes,
            CancellationToken cancellationToken = default);
    }
}
