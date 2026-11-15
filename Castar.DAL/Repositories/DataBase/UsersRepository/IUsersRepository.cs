using Castar.Domain.Models.DataBase;

namespace Castar.DAL.Repositories.DataBase
{
    public interface IUsersRepository : IBaseRepository<User>
    {
        Task<IReadOnlyList<User>> List(bool hasTelegramId = false, CancellationToken cancellationToken = default);
        Task<User?> FindByTelegramId(long telegramId, CancellationToken cancellationToken = default);
    }
}
