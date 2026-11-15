using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public sealed class ItemsRepository(IDbContextFactory<CastarDbContext> contextFactory)
        : BaseRepository<Item>(contextFactory), IItemsRepository
    {
        public async Task<IReadOnlyList<Item>> List(DateTime start, DateTime finish, bool includeUser)
        {
            await using var db = await ContextFactory.CreateDbContextAsync();
            var items = db.Items.AsNoTrackingWithIdentityResolution()
                .Where(x => x.Purchase!.AddedOn >= start && x.Purchase.AddedOn <= finish);

            if (!includeUser)
                return await items.Include(x => x.Purchase).ToListAsync();

            var result = await items.Include(x => x.Purchase).ThenInclude(x => x!.User).ToListAsync();
            foreach (var item in result)
                item.Users.Add(item.Purchase!.User);
            return result;
        }

        public override async Task<Item> Add(Item item, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            var purchase = await FindPurchase(db, item.PurchaseId, cancellationToken);
            PurchasesRepository.ThrowIfFiscal(db, purchase);
            item.Purchase = purchase;
            purchase.Items.Add(item);
            db.Items.Add(item);
            db.Entry(item).Property<int?>(CastarDbContext.LineNumber).CurrentValue =
                purchase.Items.Max(x => db.Entry(x).Property<int?>(CastarDbContext.LineNumber).CurrentValue) + 1 ?? 0;
            purchase.RecalculateManualTotals();
            await db.SaveChangesAsync(cancellationToken);
            return item;
        }

        public override async Task Update(Item item, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            var purchase = await FindPurchase(db, item.PurchaseId, cancellationToken);
            PurchasesRepository.ThrowIfFiscal(db, purchase);
            var current = purchase.Items.FirstOrDefault(x => x.Id == item.Id)
                ?? throw new InvalidOperationException("Товар больше не принадлежит этой покупке.");
            current.Name = item.Name;
            current.Category = item.Category;
            current.Quantity = item.Quantity;
            current.Price = item.Price;
            purchase.RecalculateManualTotals();
            await db.SaveChangesAsync(cancellationToken);
            item.Sum = current.Sum;
        }

        public override async Task<bool> Delete(Guid id, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            var purchaseId = await db.Items.Where(x => x.Id == id).Select(x => (Guid?)x.PurchaseId)
                .SingleOrDefaultAsync(cancellationToken);
            if (purchaseId is null)
                return false;

            var purchase = await FindPurchase(db, purchaseId.Value, cancellationToken);
            PurchasesRepository.ThrowIfFiscal(db, purchase);
            var item = purchase.Items.FirstOrDefault(x => x.Id == id);
            if (item is null)
                return false;

            purchase.Items.Remove(item);
            db.Items.Remove(item);
            purchase.RecalculateManualTotals();
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        private static async Task<Purchase> FindPurchase(
            CastarDbContext db, Guid id, CancellationToken cancellationToken) =>
            await db.Purchases.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Покупка больше не существует.");
    }
}
