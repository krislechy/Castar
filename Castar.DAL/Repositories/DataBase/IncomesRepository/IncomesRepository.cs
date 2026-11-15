using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public sealed class IncomesRepository(IDbContextFactory<CastarDbContext> contextFactory)
        : BaseRepository<Income>(contextFactory), IIncomesRepository
    {
        public async Task<IReadOnlyList<Income>> List(DateTime start, DateTime finish, bool includeUser)
        {
            await using var db = await ContextFactory.CreateDbContextAsync();
            var incomes = db.Incomes.AsNoTracking().Where(x => x.AddedOn >= start && x.AddedOn <= finish);
            if (includeUser)
                incomes = incomes.AsNoTrackingWithIdentityResolution().Include(x => x.User);
            return await incomes.ToListAsync();
        }

        public async Task<IReadOnlyList<Income>> ListByMessage(
            Guid userId, long messageId, CancellationToken cancellationToken)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            return await ByMessage(db, userId, messageId).AsNoTracking()
                .OrderBy(x => EF.Property<int?>(x, CastarDbContext.LineNumber))
                .ThenBy(x => x.CreatedOn).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        }

        public async Task SaveMessageIncomes(
            Guid userId, long messageId, IReadOnlyList<Income> incomes, CancellationToken cancellationToken)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            var existing = await ByMessage(db, userId, messageId).ToDictionaryAsync(x => x.Id, cancellationToken);
            for (var index = 0; index < incomes.Count; index++)
            {
                var income = incomes[index];
                if (income.Id == Guid.Empty)
                {
                    income.UserId = userId;
                    income.MessageId = messageId;
                    db.Entry(income).State = EntityState.Added;
                    db.Entry(income).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index;
                }
                else if (existing.Remove(income.Id, out var current))
                {
                    current.Source = income.Source;
                    current.Sum = income.Sum;
                    db.Entry(current).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index;
                }
                else
                {
                    throw new InvalidOperationException("Начисление больше не принадлежит этому сообщению.");
                }
            }

            db.Incomes.RemoveRange(existing.Values);
            await db.SaveChangesAsync(cancellationToken);
        }

        private static IQueryable<Income> ByMessage(CastarDbContext db, Guid userId, long messageId) =>
            db.Incomes.Where(x => x.UserId == userId && EF.Property<long?>(x, CastarDbContext.MessageIdField) == messageId);
    }
}
