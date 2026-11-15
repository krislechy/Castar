using System.Security.Cryptography;
using System.Text;
using Castar.Domain.Models.DataBase;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public sealed class PurchasesRepository(IDbContextFactory<CastarDbContext> contextFactory)
        : BaseRepository<Purchase>(contextFactory), IPurchasesRepository
    {
        public async Task<IReadOnlyList<Purchase>> List(DateTime start, DateTime finish, bool includeDetails)
        {
            await using var db = await ContextFactory.CreateDbContextAsync();
            var purchases = db.Purchases.AsNoTracking().Where(x => x.AddedOn >= start && x.AddedOn <= finish);
            if (includeDetails)
                purchases = WithDetails(purchases);
            return await purchases.ToListAsync();
        }

        public async Task<Purchase?> FindManualByMessage(Guid userId, long messageId, CancellationToken cancellationToken)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            return await WithDetails(db.Purchases)
                .Where(x => x.UserId == userId && EF.Property<long?>(x, CastarDbContext.MessageIdField) == messageId
                    && x.RawData == null && EF.Property<byte[]?>(x, CastarDbContext.ReceiptKey) == null)
                .OrderBy(x => x.CreatedOn).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        }

        public override async Task<Purchase> Add(Purchase purchase, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            purchase.RecalculateManualTotals();
            db.Purchases.Add(purchase);
            AssignLineNumbers(db, purchase.Items);
            await db.SaveChangesAsync(cancellationToken);
            return purchase;
        }

        public async Task<Purchase?> AddReceipt(
            Purchase purchase, string provider, string receiptId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);

            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            if (purchase.RawData is { Length: > 0 } rawData)
            {
                purchase.RawData = PadRawData(rawData);
                if (await db.Purchases.AnyAsync(x => x.RawData == purchase.RawData, cancellationToken))
                    return null;
            }

            var key = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(provider, "\0", receiptId)));
            db.Purchases.Add(purchase);
            AssignLineNumbers(db, purchase.Items);
            db.Entry(purchase).Property<byte[]>(CastarDbContext.ReceiptKey).CurrentValue = key;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return purchase;
            }
            catch (DbUpdateException exception) when (IsDuplicateReceipt(exception.InnerException))
            {
                return null;
            }
        }

        public override async Task Update(Purchase purchase, CancellationToken cancellationToken = default)
        {
            await using var db = await ContextFactory.CreateDbContextAsync(cancellationToken);
            var current = await db.Purchases.Include(x => x.Items)
                .SingleOrDefaultAsync(x => x.Id == purchase.Id, cancellationToken)
                ?? throw new InvalidOperationException("Покупка больше не существует.");

            ThrowIfFiscal(db, current);
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
                    db.Items.Add(item);
                    db.Entry(item).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index;
                }
                else if (existing.Remove(item.Id, out var currentItem))
                {
                    currentItem.Name = item.Name;
                    currentItem.Price = item.Price;
                    currentItem.Quantity = item.Quantity;
                    currentItem.Category = item.Category;
                    db.Entry(currentItem).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index;
                }
                else
                {
                    throw new InvalidOperationException("Товар больше не принадлежит этой покупке.");
                }
            }

            foreach (var removed in existing.Values)
            {
                current.Items.Remove(removed);
                db.Items.Remove(removed);
            }

            current.RecalculateManualTotals();
            await db.SaveChangesAsync(cancellationToken);
        }

        public async Task<bool> SearchByRawData(byte[]? query)
        {
            if (query is not { Length: > 0 and <= 100 })
                return false;

            await using var db = await ContextFactory.CreateDbContextAsync();
            var padded = PadRawData(query);
            return await db.Purchases.AnyAsync(x => x.RawData == padded);
        }

        private static IQueryable<Purchase> WithDetails(IQueryable<Purchase> purchases) =>
            purchases.AsNoTrackingWithIdentityResolution().Include(x => x.User)
                .Include(x => x.Items.OrderBy(i => EF.Property<int?>(i, CastarDbContext.LineNumber))
                    .ThenBy(i => i.CreatedOn).ThenBy(i => i.Id));

        private static void AssignLineNumbers(CastarDbContext db, IEnumerable<Item> items)
        {
            var index = 0;
            foreach (var item in items)
                db.Entry(item).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index++;
        }

        internal static void ThrowIfFiscal(CastarDbContext db, Purchase purchase)
        {
            if (purchase.RawData is not null || db.Entry(purchase).Property<byte[]>(CastarDbContext.ReceiptKey).CurrentValue is not null)
                throw new InvalidOperationException("Позиции фискального чека нельзя редактировать вручную.");
        }

        private static bool IsDuplicateReceipt(Exception? exception) => exception switch
        {
            SqlException { Number: 2601 or 2627 } sql =>
                sql.Message.Contains(CastarDbContext.ReceiptIndex, StringComparison.Ordinal),
            SqliteException { SqliteExtendedErrorCode: 2067 } sqlite =>
                sqlite.Message.Contains("Purchases.ReceiptKey", StringComparison.Ordinal),
            _ => false
        };

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
