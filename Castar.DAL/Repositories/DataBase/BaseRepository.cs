using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public abstract class BaseRepository<T>(IDbContextFactory<CastarDbContext> contextFactory) : IBaseRepository<T>
        where T : BaseDbModel
    {
        protected IDbContextFactory<CastarDbContext> ContextFactory { get; } = contextFactory;

        public virtual async Task<T> Add(T entity, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            db.Entry(entity).State = EntityState.Added;
            await db.SaveChangesAsync(cancellationToken);
            return entity;
        }

        public virtual async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            return await db.Set<T>().Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
        }

        public async Task<T?> Find(Guid id, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            return await db.Set<T>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        }

        public virtual async Task Update(T entity, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            db.Entry(entity).State = EntityState.Modified;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
