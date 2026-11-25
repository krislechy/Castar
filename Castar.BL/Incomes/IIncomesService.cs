using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public interface IIncomesService
    {
        Task<Income> Add(Income income, long telegramUserId, User? user = null, CancellationToken cancellationToken = default);
        Task<bool> Delete(Guid id, CancellationToken cancellationToken = default);
        Task Update(Income income, bool updateCategory = false, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Income>> GetEntityByMessageId(
            long telegramUserId, long messageId, CancellationToken cancellationToken = default);
        Task SaveMessageIncomes(long telegramUserId, long messageId, IReadOnlyList<Income> incomes,
            CancellationToken cancellationToken = default);
    }
}
