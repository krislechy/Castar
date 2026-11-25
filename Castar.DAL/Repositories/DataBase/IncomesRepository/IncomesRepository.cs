using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public sealed partial class IncomesRepository(CastarDbContext db, IncomeWriteCommitted? publish = null)
        : BaseRepository<Income>(db), IIncomesRepository
    {
        public async Task<IReadOnlyList<IncomeListEntry>> ListAsync(DateTime start, DateTime finish, CancellationToken cancellationToken) =>
            await Context.Incomes.AsNoTracking().Where(income => income.AddedOn >= start && income.AddedOn <= finish)
                .Select(income => new IncomeListEntry(income, new IncomeOwner(income.User.Id, income.User.TelegramId,
                    income.User.Name, income.User.Sex, income.User.ColorRaw, income.User.TelegramNotificationsEnabled,
                    income.User.ImageRaw != null && income.User.ImageRaw.Length != 0)))
                .ToListAsync(cancellationToken);

        public async Task<IReadOnlyList<Income>> ListByMessage(
            Guid userId, long messageId, CancellationToken cancellationToken)
        {
            return await ByMessage(Context, userId, messageId).AsNoTracking()
                .OrderBy(x => EF.Property<int?>(x, CastarDbContext.LineNumber))
                .ThenBy(x => x.CreatedOn).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        }

        public override Task Update(Income income, CancellationToken cancellationToken = default) =>
            Update(income, updateCategory: false, cancellationToken);

        public override async Task<Income> Add(Income income, CancellationToken cancellationToken = default)
        {
            if (income.CategoryState != IncomeCategoryState.Confirmed && income.CategoryId is null)
            {
                income.CategoryState = IncomeCategoryState.Pending;
                return await base.Add(income, cancellationToken);
            }

            await using var transaction = Context.Database.CurrentTransaction is null
                ? await Context.Database.BeginTransactionAsync(cancellationToken) : null;
            await ConfirmCategoryAsync(income, income.CategoryId, cancellationToken);
            await base.Add(income, cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                publish?.Invoke([income], income.AddedOn);
            }
            return income;
        }

        public async Task Update(Income income, bool updateCategory, CancellationToken cancellationToken = default)
        {
            await using var transaction = Context.Database.CurrentTransaction is null
                ? await Context.Database.BeginTransactionAsync(cancellationToken) : null;
            var current = await FindForUpdate(income.Id, cancellationToken);
            var oldDate = current.AddedOn;
            current.UserId = income.UserId;
            current.MessageId = income.MessageId;
            var classificationChanged = UpdateSource(current, income.Source) ||
                current.AddedOn != income.AddedOn || updateCategory;
            current.Sum = income.Sum;
            current.Currency = income.Currency ?? current.Currency;
            current.AddedOn = income.AddedOn;
            if (updateCategory)
                await ConfirmCategoryAsync(current, income.CategoryId, cancellationToken);
            if (classificationChanged)
                current.CategoryRevision = checked(current.CategoryRevision + 1);
            var changes = await Context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                if (changes > 0)
                    publish?.Invoke([], oldDate < current.AddedOn ? oldDate : current.AddedOn);
            }
            income.Currency = current.Currency;
            income.RateDate = current.RateDate;
            income.CategoryId = current.CategoryId;
            income.CategoryState = current.CategoryState;
            income.CategoryRevision = current.CategoryRevision;
        }

        public override async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
        {
            await using var transaction = Context.Database.CurrentTransaction is null
                ? await Context.Database.BeginTransactionAsync(cancellationToken) : null;
            var income = await Context.Incomes.FindAsync([id], cancellationToken);
            if (income is null)
                return false;

            Context.Incomes.Remove(income);
            await Context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                publish?.Invoke([], income.AddedOn);
            }
            return true;
        }

        public async Task SaveMessageIncomes(
            Guid userId, long messageId, IReadOnlyList<Income> incomes, CancellationToken cancellationToken)
        {
            await using var transaction = Context.Database.CurrentTransaction is null
                ? await Context.Database.BeginTransactionAsync(cancellationToken) : null;
            var existing = await ByMessage(Context, userId, messageId).ToDictionaryAsync(x => x.Id, cancellationToken);
            var created = new List<Income>();
            for (var index = 0; index < incomes.Count; index++)
            {
                var income = incomes[index];
                if (income.Id == Guid.Empty)
                {
                    income.UserId = userId;
                    income.MessageId = messageId;
                    income.CategoryState = IncomeCategoryState.Pending;
                    Context.Entry(income).State = EntityState.Added;
                    Context.Entry(income).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index;
                    created.Add(income);
                }
                else if (existing.Remove(income.Id, out var current))
                {
                    if (UpdateSource(current, income.Source))
                        current.CategoryRevision = checked(current.CategoryRevision + 1);
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
            var affectedFrom = Context.ChangeTracker.Entries<Income>()
                .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .Min(entry => (DateTime?)entry.Entity.AddedOn);
            var changes = await Context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                if (changes > 0)
                    publish?.Invoke(created, affectedFrom);
            }
        }

        private static IQueryable<Income> ByMessage(CastarDbContext db, Guid userId, long messageId) =>
            db.Incomes.Where(x => x.UserId == userId && EF.Property<long?>(x, CastarDbContext.MessageIdField) == messageId);
    }
}
