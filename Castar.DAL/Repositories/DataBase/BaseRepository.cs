using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public abstract class BaseRepository<T>(CastarDbContext db) : IBaseRepository<T>
        where T : BaseDbModel
    {
        protected CastarDbContext Context { get; } = db;

        public virtual async Task<T> Add(T entity, CancellationToken cancellationToken = default)
        {
            // Related records are resolved by the service and are already in the database.
            Context.Entry(entity).State = EntityState.Added;
            await Context.SaveChangesAsync(cancellationToken);
            return entity;
        }

        public virtual async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
        {
            var entity = await Context.Set<T>().FindAsync([id], cancellationToken);
            if (entity is null)
                return false;

            Context.Set<T>().Remove(entity);
            await Context.SaveChangesAsync(cancellationToken);
            return true;
        }

        public Task<T?> Find(Guid id, CancellationToken cancellationToken = default) =>
            Context.Set<T>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        public abstract Task Update(T entity, CancellationToken cancellationToken = default);

        protected async ValueTask<T> FindForUpdate(Guid id, CancellationToken cancellationToken) =>
            await Context.Set<T>().FindAsync([id], cancellationToken)
            ?? throw new DbUpdateConcurrencyException("Запись больше не существует.");
    }
}
