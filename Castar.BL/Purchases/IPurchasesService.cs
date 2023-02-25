using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.BL
{
    public interface IPurchasesService
    {
        Task<ObservableCollection<Purchase>> GetEntities(DateTime start, DateTime finish);
        Task<ObservableCollection<Purchase>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish);
        Task<Purchase?> GetPurchaseById(Guid id);
        Task<bool> IsExistTheSame(byte[] query);
        Task<Purchase> Add(Purchase purchase, long telegramUserId);
        Task<IEnumerable<Purchase>> GetEntityByMessageId(long messageId);
        Task Update(Purchase income);
        Task Delete(Guid id);
    }
}
