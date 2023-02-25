using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.Services.Receipts
{
    public interface IReceipt<TEntity> where TEntity : class
    {
        Task<TEntity> GetReceipt(string query);
    }
}
