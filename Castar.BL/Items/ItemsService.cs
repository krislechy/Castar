using System.Collections.ObjectModel;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;

namespace Castar.BL
{
    public sealed class ItemsService(IItemsRepository itemRepository) : IItemsService
    {
        public async Task<ObservableCollection<Item>> GetEntities(DateTime start, DateTime finish) =>
            new(await itemRepository.List(start, finish, includeUser: false));

        public async Task<ObservableCollection<Item>> GetEntitiesWithAllIncludes(DateTime start, DateTime finish) =>
            new(await itemRepository.List(start, finish, includeUser: true));

        public Task Add(Item item) => itemRepository.Add(item);

        public Task Update(Item item) => itemRepository.Update(item);

        public Task Delete(Guid id) => itemRepository.Delete(id);
    }
}
