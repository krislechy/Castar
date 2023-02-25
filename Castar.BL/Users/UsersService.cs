using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Castar.BL
{
    public class UsersService : IUsersService
    {
        private readonly IUsersRepository usersRepository;
        public UsersService(IUsersRepository usersRepository)
        {
            this.usersRepository = usersRepository;
        }

        public async Task<ObservableCollection<User>> GetEntities() =>
             new ObservableCollection<User>(await usersRepository.GetEntity()
                .ToListAsync());

        public async void Add(User user)=>
            await usersRepository.Add(user);

        public async Task<IEnumerable<User>> GetAllUsersWithTelegramId()
        {
            return await usersRepository
                .GetEntity()
                .Where(x => x.TelegramId != default)
                .ToListAsync();
        }
        public async Task<User?> GetUserByTelegramId(long id)
        {
            return await usersRepository
                .GetEntity()
                .Where(x => x.TelegramId == id)
                .FirstOrDefaultAsync();
        }
        public async void Update(User user)
        {
            await usersRepository.Update(user);
        }
        public async Task<bool> Delete(Guid id)
        {
            return await usersRepository.Delete(id);
        }
    }
}
