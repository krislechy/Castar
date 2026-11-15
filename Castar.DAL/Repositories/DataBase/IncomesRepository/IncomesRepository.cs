using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public sealed class IncomesRepository(CastarDbContext db)
        : BaseRepository<Income>(db), IIncomesRepository
    {
        public async Task<IReadOnlyList<Income>> List(DateTime start, DateTime finish, bool includeUser)
        {
            var incomes = Context.Incomes.AsNoTracking().Where(x => x.AddedOn >= start && x.AddedOn <= finish);
            if (includeUser)
                incomes = incomes.AsNoTrackingWithIdentityResolution().Include(x => x.User);
            return await incomes.ToListAsync();
        }

        public async Task<IReadOnlyList<Income>> ListByMessage(
            Guid userId, long messageId, CancellationToken cancellationToken)
        {
            return await ByMessage(Context, userId, messageId).AsNoTracking()
                .OrderBy(x => EF.Property<int?>(x, CastarDbContext.LineNumber))
                .ThenBy(x => x.CreatedOn).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        }

        public override async Task Update(Income income, CancellationToken cancellationToken = default)
        {
            var current = await FindForUpdate(income.Id, cancellationToken);
            current.UserId = income.UserId;
            current.MessageId = income.MessageId;
            current.Source = income.Source;
            current.Sum = income.Sum;
            current.Currency = income.Currency ?? current.Currency;
            current.AddedOn = income.AddedOn;
            await Context.SaveChangesAsync(cancellationToken);
            income.Currency = current.Currency;
        }

        public async Task SaveMessageIncomes(
            Guid userId, long messageId, IReadOnlyList<Income> incomes, CancellationToken cancellationToken)
        {
            var existing = await ByMessage(Context, userId, messageId).ToDictionaryAsync(x => x.Id, cancellationToken);
            for (var index = 0; index < incomes.Count; index++)
            {
                var income = incomes[index];
                if (income.Id == Guid.Empty)
                {
                    income.UserId = userId;
                    income.MessageId = messageId;
                    Context.Entry(income).State = EntityState.Added;
                    Context.Entry(income).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index;
                }
                else if (existing.Remove(income.Id, out var current))
                {
                    current.Source = income.Source;
                    current.Sum = income.Sum;
                    current.Currency = income.Currency ?? current.Currency;
                    Context.Entry(current).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index;
                }
                else
                {
                    throw new InvalidOperationException("Начисление больше не принадлежит этому сообщению.");
                }
            }

            Context.Incomes.RemoveRange(existing.Values);
            await Context.SaveChangesAsync(cancellationToken);
        }

        private static IQueryable<Income> ByMessage(CastarDbContext db, Guid userId, long messageId) =>
            db.Incomes.Where(x => x.UserId == userId && EF.Property<long?>(x, CastarDbContext.MessageIdField) == messageId);
    }
}
