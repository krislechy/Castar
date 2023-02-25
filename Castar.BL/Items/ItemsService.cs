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
    public class ItemsService : IItemsService
    {
        private readonly IItemsRepository itemRepository;
        public ItemsService(IItemsRepository itemRepository)
        {
            this.itemRepository = itemRepository;
        }

        public async Task<ObservableCollection<Item>> GetEntities(DateTime start, DateTime finish) =>
             new ObservableCollection<Item>(await itemRepository.GetEntity()
                 .Include(u => u.Purchase)
                .Where(x => x.Purchase.AddedOn >= start && x.Purchase.AddedOn <= finish)
                .ToListAsync());

        public async Task<ObservableCollection<Item>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish) =>
            new ObservableCollection<Item>(await itemRepository.GetEntity()
                .Include(u => u.Purchase)
                .Where(x => x.Purchase.AddedOn >= start && x.Purchase.AddedOn <= finish)
                .Include(u=>u.Users)
                .ToListAsync());

        public async Task Add(Item item)
        {
            await itemRepository.Add(item);
        }
        public async Task Update(Item item)
        {
            await itemRepository.Update(item);
        }
        public async Task Delete(Guid id)
        {
            await itemRepository.Delete(id);
        }
    }
}
