using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.DAL.Repositories.DataBase
{
    public interface IBaseRepository<T> where T : class
    {
        public Task<int> Save();
        Task<T> Add(T entity);
        public Task<bool> Delete(Guid Id);
        Task<T?> Find(Guid Id);
        Task Update(T entity);
        void Reload(T entity);
        abstract DbSet<T> GetEntity();
    }
}
