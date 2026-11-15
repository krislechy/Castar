using Castar.Domain.Models.DataBase;

namespace Castar.DAL.Repositories.DataBase
{
    public interface IBaseRepository<T> where T : BaseDbModel
    {
        Task<T> Add(T entity, CancellationToken cancellationToken = default);
        Task<bool> Delete(Guid id, CancellationToken cancellationToken = default);
        Task<T?> Find(Guid id, CancellationToken cancellationToken = default);
        Task Update(T entity, CancellationToken cancellationToken = default);
    }
}
