using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.BL
{
    public interface IUsersService
    {
        Task<ObservableCollection<User>> GetEntities();
        void Add(User user);
        Task<IEnumerable<User>> GetAllUsersWithTelegramId();
        Task<User?> GetUserByTelegramId(long id);
        void Update(User user);
        Task<bool> Delete(Guid id);
    }
}
