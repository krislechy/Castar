using System.Security.Cryptography;
using System.Text;
using Castar.Domain.Models.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public sealed class PurchasesRepository(CastarDbContext db)
        : BaseRepository<Purchase>(db), IPurchasesRepository
    {
        public async Task<IReadOnlyList<Purchase>> List(DateTime start, DateTime finish, bool includeDetails)
        {
            var purchases = Context.Purchases.AsNoTracking().Where(x => x.AddedOn >= start && x.AddedOn <= finish);
            if (includeDetails)
                purchases = WithDetails(purchases);
            return await purchases.ToListAsync();
        }

        public Task<Purchase?> FindManualByMessage(Guid userId, long messageId, CancellationToken cancellationToken) =>
            WithDetails(Context.Purchases)
                .Where(x => x.UserId == userId && EF.Property<long?>(x, CastarDbContext.MessageIdField) == messageId
                    && x.RawData == null && EF.Property<byte[]?>(x, CastarDbContext.ReceiptKey) == null)
                .OrderBy(x => x.CreatedOn).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        public override async Task<Purchase> Add(Purchase purchase, CancellationToken cancellationToken = default)
        {
            purchase.RecalculateManualTotals();
            TrackNewPurchase(purchase);
            await Context.SaveChangesAsync(cancellationToken);
            return purchase;
        }

        public async Task<Purchase?> AddReceipt(
            Purchase purchase, string provider, string receiptId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);

            if (purchase.RawData is { Length: > 0 } rawData)
            {
                purchase.RawData = PadRawData(rawData);
                if (await Context.Purchases.AnyAsync(x => x.RawData == purchase.RawData, cancellationToken))
                    return null;
            }

            var key = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(provider, "\0", receiptId)));
            TrackNewPurchase(purchase);
            Context.Entry(purchase).Property<byte[]>(CastarDbContext.ReceiptKey).CurrentValue = key;
            try
            {
                await Context.SaveChangesAsync(cancellationToken);
                return purchase;
            }
            catch (DbUpdateException exception) when (IsDuplicateReceipt(exception.InnerException))
            {
                // A Telegram update may contain several receipts. Drop only the failed insert.
                foreach (var item in purchase.Items.ToArray())
                    Context.Entry(item).State = EntityState.Detached;
                Context.Entry(purchase).State = EntityState.Detached;
                return null;
            }
        }

        public override async Task Update(Purchase purchase, CancellationToken cancellationToken = default)
        {
            var current = await Context.Purchases.Include(x => x.Items)
                .SingleOrDefaultAsync(x => x.Id == purchase.Id, cancellationToken)
                ?? throw new InvalidOperationException("Покупка больше не существует.");

            ThrowIfFiscal(Context, current);
            current.Name = purchase.Name;
            current.RetailPlace = purchase.RetailPlace;
            current.RetailPlaceAddress = purchase.RetailPlaceAddress;
            current.AddedOn = purchase.AddedOn;

            var existing = current.Items.ToDictionary(x => x.Id);
            for (var index = 0; index < purchase.Items.Count; index++)
            {
                var item = purchase.Items[index];
                if (item.Id == Guid.Empty)
                {
                    item.PurchaseId = purchase.Id;
                    item.Purchase = current;
                    Context.Items.Add(item);
                    Context.Entry(item).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index;
                }
                else if (existing.Remove(item.Id, out var currentItem))
                {
                    currentItem.Name = item.Name;
                    currentItem.Price = item.Price;
                    currentItem.Quantity = item.Quantity;
                    currentItem.Category = item.Category;
                    Context.Entry(currentItem).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index;
                }
                else
                {
                    throw new InvalidOperationException("Товар больше не принадлежит этой покупке.");
                }
            }

            foreach (var removed in existing.Values)
            {
                current.Items.Remove(removed);
                Context.Items.Remove(removed);
            }

            current.RecalculateManualTotals();
            await Context.SaveChangesAsync(cancellationToken);
        }

        public async Task<bool> SearchByRawData(byte[]? query)
        {
            if (query is not { Length: > 0 and <= 100 })
                return false;

            var padded = PadRawData(query);
            return await Context.Purchases.AnyAsync(x => x.RawData == padded);
        }

        private static IQueryable<Purchase> WithDetails(IQueryable<Purchase> purchases) =>
            purchases.AsNoTrackingWithIdentityResolution().Include(x => x.User)
                .Include(x => x.Items.OrderBy(i => EF.Property<int?>(i, CastarDbContext.LineNumber))
                    .ThenBy(i => i.CreatedOn).ThenBy(i => i.Id));

        private void TrackNewPurchase(Purchase purchase)
        {
            Context.Entry(purchase).State = EntityState.Added;
            var index = 0;
            foreach (var item in purchase.Items)
            {
                item.PurchaseId = purchase.Id;
                item.Purchase = purchase;
                Context.Entry(item).State = EntityState.Added;
                Context.Entry(item).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index++;
            }
        }

        internal static void ThrowIfFiscal(CastarDbContext db, Purchase purchase)
        {
            if (purchase.RawData is not null || db.Entry(purchase).Property<byte[]>(CastarDbContext.ReceiptKey).CurrentValue is not null)
                throw new InvalidOperationException("Позиции фискального чека нельзя редактировать вручную.");
        }

        private static bool IsDuplicateReceipt(Exception? exception) =>
            exception is SqliteException { SqliteExtendedErrorCode: 2067 } sqlite
            && sqlite.Message.Contains("Purchases.ReceiptKey", StringComparison.Ordinal);

        private static byte[] PadRawData(byte[] rawData)
        {
            // Historical binary(100) values contain right padding.
            if (rawData.Length > 100)
                throw new ArgumentException("Длина исходного формата чека превышает 100 байт.", nameof(rawData));

            var padded = new byte[100];
            rawData.AsSpan().CopyTo(padded);
            return padded;
        }
    }
}
