using System.Collections.ObjectModel;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public interface IUsersService
    {
        Task<ObservableCollection<User>> GetEntities();
        Task Add(User user);
        Task<IEnumerable<User>> GetAllUsersWithTelegramId(CancellationToken cancellationToken = default);
        Task<User?> GetUserByTelegramId(long id, CancellationToken cancellationToken = default);
        Task Update(User user);
        Task<bool> Delete(Guid id);
    }
}
