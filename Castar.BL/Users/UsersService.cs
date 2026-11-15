using System.Collections.ObjectModel;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public sealed class UsersService(IUsersRepository usersRepository) : IUsersService
    {
        public async Task<ObservableCollection<User>> GetEntities() => new(await usersRepository.List());

        public Task Add(User user) => usersRepository.Add(user);

        public async Task<IEnumerable<User>> GetAllUsersWithTelegramId(CancellationToken cancellationToken = default) =>
            await usersRepository.List(hasTelegramId: true, cancellationToken);

        public Task<User?> GetUserByTelegramId(long id, CancellationToken cancellationToken = default) =>
            usersRepository.FindByTelegramId(id, cancellationToken);

        public Task Update(User user) => usersRepository.Update(user);

        public Task<bool> Delete(Guid id) => usersRepository.Delete(id);
    }
}
