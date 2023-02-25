using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.DAL.Repositories.DataBase
{
    public interface IUsersRepository : IBaseRepository<User>
    {
        Task<User?> FindByTelegramId(long telegramId);
    }
}
