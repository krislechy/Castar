using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public interface IUsersService
    {
        Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken);
        Task Add(User user, CancellationToken cancellationToken = default);
        Task<IEnumerable<User>> GetAllUsersWithTelegramId(CancellationToken cancellationToken = default);
        Task<User?> GetUserByTelegramId(long id, CancellationToken cancellationToken = default);
        Task Update(User user, CancellationToken cancellationToken = default);
        Task<bool> Delete(Guid id, CancellationToken cancellationToken = default);
    }
}
