using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.DAL.Repositories.DataBase
{
    public class UsersRepository : BaseRepository<User>, IUsersRepository
    {
        public UsersRepository(string connectionString):base(connectionString)
        {

        }

        public override DbSet<User> GetEntity()
        {
            //if(!base.table.Any())
            //{
            //    return default!;
            //}
            return base.table;
        }
        public async Task<User?> FindByTelegramId(long telegramId)
        {
            var entities = table.Where(x => x.TelegramId == telegramId);
            if ((await entities.CountAsync()) > 0)
                return await entities.FirstAsync();
            return null;
        }
    }
}
