using Castar.Domain.Models.DataBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Castar.BL
{
    public interface IItemsService
    {
        Task<ObservableCollection<Item>> GetEntities(DateTime start, DateTime finish);
        Task<ObservableCollection<Item>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish);
        Task Add(Item user);
        Task Update(Item item);
        Task Delete(Guid id);
    }
}
