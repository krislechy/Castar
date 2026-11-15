using Castar.Domain.Models.DataBase;

namespace Castar.DAL.Repositories.DataBase
{
    public interface IItemsRepository : IBaseRepository<Item>
    {
        Task<IReadOnlyList<Item>> List(DateTime start, DateTime finish, bool includeUser);
    }
}
