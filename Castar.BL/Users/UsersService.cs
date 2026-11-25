using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public sealed class UsersService(IUsersRepository usersRepository) : IUsersService
    {
        public Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken) =>
            usersRepository.List(cancellationToken: cancellationToken);

        public Task Add(User user, CancellationToken cancellationToken = default) => usersRepository.Add(user, cancellationToken);

        public async Task<IEnumerable<User>> GetAllUsersWithTelegramId(CancellationToken cancellationToken = default) =>
            await usersRepository.List(hasTelegramId: true, cancellationToken);

        public Task<User?> GetUserByTelegramId(long id, CancellationToken cancellationToken = default) =>
            usersRepository.FindByTelegramId(id, cancellationToken);

        public Task Update(User user, CancellationToken cancellationToken = default) => usersRepository.Update(user, cancellationToken);

        public Task<bool> Delete(Guid id, CancellationToken cancellationToken = default) =>
            usersRepository.Delete(id, cancellationToken);
    }
}
