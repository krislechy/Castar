using System.Security.Cryptography;
using System.Text;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.DAL.Repositories.DataBase
{
    public sealed partial class PurchasesRepository(CastarDbContext db)
        : BaseRepository<Purchase>(db), IPurchasesRepository
    {
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public async Task<IReadOnlyList<Purchase>> List(DateTime start, DateTime finish, bool includeDetails)
        {
            var purchases = Context.Purchases.AsNoTracking().Where(x => x.AddedOn >= start && x.AddedOn <= finish);
            if (includeDetails)
                purchases = WithDetails(purchases);
            return await purchases.ToListAsync();
        }

        public Task<Purchase?> FindManualByMessage(Guid userId, long messageId, CancellationToken cancellationToken) =>
            WithDetails(Context.Purchases)
                .Where(x => x.TelegramOriginUserId == userId && EF.Property<long?>(x, CastarDbContext.MessageIdField) == messageId
                    && x.RawData == null && EF.Property<byte[]?>(x, CastarDbContext.ReceiptKey) == null)
                .OrderBy(x => x.CreatedOn).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);

        public Task<Purchase?> FindWithItemsAsync(Guid id, CancellationToken cancellationToken) =>
            WithItems(Context.Purchases.AsNoTracking()).SingleOrDefaultAsync(purchase => purchase.Id == id, cancellationToken);
        public override async Task<Purchase> Add(Purchase purchase, CancellationToken cancellationToken = default)
        {
            purchase.RecalculateManualTotals();
            await using var transaction = purchase.Items.Any(item => item.CategoryState == ItemCategoryState.Confirmed) &&
                Context.Database.CurrentTransaction is null ? await Context.Database.BeginTransactionAsync(cancellationToken) : null;
            TrackNewPurchase(purchase);
            foreach (var item in purchase.Items.Where(item => item.CategoryState == ItemCategoryState.Confirmed))
                await ConfirmCategoryAsync(purchase, item, item.BudgetCategoryId, cancellationToken);
            await Context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return purchase;
        }

        public async Task<Purchase?> AddReceipt(
            Purchase purchase, string provider, string receiptId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);

            if (purchase.RawData is { Length: > 100 } && purchase.ReceiptSource is not null)
            {
                // The legacy column cannot contain long QR codes. The full source and receipt key still persist.
                purchase.RawData = null;
            }
            if (purchase.RawData is { Length: > 0 } rawData)
            {
                purchase.RawData = PadRawData(rawData);
                if (await Context.Purchases.AnyAsync(x => x.RawData == purchase.RawData, cancellationToken))
                    return null;
            }

            var key = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(provider, "\0", receiptId)));
            purchase.PreserveImportedTotal();
            TrackNewPurchase(purchase);
            Context.Entry(purchase).Property<byte[]>(CastarDbContext.ReceiptKey).CurrentValue = key;
            PreserveOriginalReceipt(purchase);
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
                if (purchase.Adjustment is { } adjustment)
                    Context.Entry(adjustment).State = EntityState.Detached;
                Context.Entry(purchase).State = EntityState.Detached;
                return null;
            }
        }

        public override async Task Update(Purchase purchase, CancellationToken cancellationToken = default)
        {
            var current = await WithItems(Context.Purchases)
                .SingleOrDefaultAsync(x => x.Id == purchase.Id, cancellationToken)
                ?? throw new InvalidOperationException("Покупка больше не существует.");

            ThrowIfFiscal(Context, current);
            if (current.Revision != purchase.Revision)
                throw new DbUpdateConcurrencyException("Покупка уже изменилась.");
            current.Name = purchase.Name;
            var merchantChanged = NormalizeCategoryKeyPart(current.RetailPlace) != NormalizeCategoryKeyPart(purchase.RetailPlace);
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
                    if (merchantChanged || NormalizeCategoryKeyPart(currentItem.Name) != NormalizeCategoryKeyPart(item.Name))
                        ResetAutomaticCategory(currentItem);
                    currentItem.Name = item.Name;
                    currentItem.Price = item.Price;
                    currentItem.Quantity = item.Quantity;
                    currentItem.Currency = item.Currency;
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
            current.Revision = checked(current.Revision + 1);
            await Context.SaveChangesAsync(cancellationToken);
            purchase.Revision = current.Revision;
        }

        public async Task<(bool Found, string? ReceiptSource, bool IsLegacy)> GetReceiptSourceAsync(
            Guid purchaseId, CancellationToken cancellationToken = default)
        {
            var purchase = await Context.Purchases.AsNoTracking().Where(x => x.Id == purchaseId)
                .Select(x => new { x.ReceiptSource, x.RawData }).SingleOrDefaultAsync(cancellationToken);
            if (purchase is null)
                return (false, null, false);
            if (!string.IsNullOrWhiteSpace(purchase.ReceiptSource))
                return (true, purchase.ReceiptSource, false);
            var legacy = DecodeLegacySource(purchase.RawData);
            return (true, legacy, legacy is not null);
        }

        public static bool HasReceiptSource(Purchase purchase) =>
            !string.IsNullOrWhiteSpace(purchase.ReceiptSource) || DecodeLegacySource(purchase.RawData) is not null;

        public async Task<PurchaseMutationResult> DeletePurchaseAsync(
            Guid purchaseId, long expectedRevision, CancellationToken cancellationToken = default,
            bool supportsItemCurrency = true)
        {
            var query = supportsItemCurrency ? Context.Purchases : WithItems(Context.Purchases);
            var purchase = await query.SingleOrDefaultAsync(x => x.Id == purchaseId, cancellationToken);
            if (purchase is null)
                return PurchaseMutationResult.NotFound;
            if (purchase.Revision != expectedRevision)
                return PurchaseMutationResult.Conflict;
            if (!supportsItemCurrency && purchase.HasMultipleCurrencies())
                return PurchaseMutationResult.UnsupportedItemCurrency;

            // The database cascade removes the items in the same write as the purchase.
            Context.Purchases.Remove(purchase);
            await Context.SaveChangesAsync(cancellationToken);
            return PurchaseMutationResult.Applied;
        }

        public async Task<PurchaseMutationResult> DeletePurchaseItemAsync(
            Guid purchaseId, Guid itemId, long expectedRevision, CancellationToken cancellationToken = default,
            bool supportsItemCurrency = true)
        {
            var purchase = await WithItems(Context.Purchases)
                .SingleOrDefaultAsync(x => x.Id == purchaseId, cancellationToken);
            if (purchase?.Items.FirstOrDefault(item => item.Id == itemId) is not { } item)
                return PurchaseMutationResult.NotFound;
            if (purchase.Revision != expectedRevision)
                return PurchaseMutationResult.Conflict;
            if (!supportsItemCurrency && purchase.HasMultipleCurrencies())
                return PurchaseMutationResult.UnsupportedItemCurrency;

            var fiscal = IsFiscal(Context, purchase);
            if (fiscal)
                PreserveOriginalReceipt(purchase);
            purchase.Items.Remove(item);
            Context.Items.Remove(item);
            purchase.Revision = checked(purchase.Revision + 1);
            await Context.SaveChangesAsync(cancellationToken);
            return PurchaseMutationResult.Applied;
        }

        public async Task<PurchaseMutationResult> EditPurchaseAsync(
            Guid purchaseId, long expectedRevision, string name, string? retailPlace, string? retailPlaceAddress,
            DateTime addedOn, string? currency = null, ManualCurrencySettings? currencyPolicy = null,
            CancellationToken cancellationToken = default, Guid? userId = null, DateTime? legacyDate = null,
            bool supportsItemCurrency = true)
        {
            var purchase = await WithItems(Context.Purchases)
                .SingleOrDefaultAsync(x => x.Id == purchaseId, cancellationToken);
            if (purchase is null)
                return PurchaseMutationResult.NotFound;
            if (purchase.Revision != expectedRevision)
                return PurchaseMutationResult.Conflict;
            if (!supportsItemCurrency && purchase.HasMultipleCurrencies())
                return PurchaseMutationResult.UnsupportedItemCurrency;
            if (legacyDate is { } local && purchase.AddedOn is { } current &&
                BudgetTime.InZone(current, BudgetTime.Belgrade) == local)
                addedOn = current;

            var ownerId = userId ?? purchase.UserId;
            var changingOwner = ownerId != purchase.UserId;
            if (changingOwner &&
                !await Context.Users.AnyAsync(owner => owner.Id == ownerId, cancellationToken))
                return PurchaseMutationResult.InvalidOwner;

            if (currency is not null)
            {
                var suppliedCurrency = ManualCurrencySettings.NormalizeCode(currency);
                if (suppliedCurrency is null)
                    return PurchaseMutationResult.InvalidCurrency;
                if (suppliedCurrency != purchase.Currency)
                    return IsFiscal(Context, purchase) ? PurchaseMutationResult.FiscalCurrency : PurchaseMutationResult.ItemCurrencyInUse;
            }

            var headerChanged = purchase.Name != name || purchase.RetailPlace != retailPlace ||
                purchase.RetailPlaceAddress != retailPlaceAddress || purchase.AddedOn != addedOn ||
                changingOwner;
            if (!headerChanged)
                return PurchaseMutationResult.Applied;

            if (IsFiscal(Context, purchase))
                PreserveOriginalReceipt(purchase);
            purchase.Name = name;
            if (NormalizeCategoryKeyPart(purchase.RetailPlace) != NormalizeCategoryKeyPart(retailPlace))
                foreach (var item in purchase.Items)
                    ResetAutomaticCategory(item);
            purchase.RetailPlace = retailPlace;
            purchase.RetailPlaceAddress = retailPlaceAddress;
            purchase.AddedOn = addedOn;
            purchase.UserId = ownerId;
            purchase.Revision = checked(purchase.Revision + 1);
            try
            {
                await Context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException error) when (changingOwner &&
                error.InnerException is SqliteException { SqliteExtendedErrorCode: 787 })
            {
                return PurchaseMutationResult.InvalidOwner;
            }
            return PurchaseMutationResult.Applied;
        }

        public async Task<PurchaseMutationResult> EditPurchaseItemAsync(
            Guid purchaseId, Guid itemId, long expectedRevision, string name, double quantity, decimal price,
            CancellationToken cancellationToken = default, Guid? categoryId = null, bool updateCategory = false,
            string? currency = null, ManualCurrencySettings? currencyPolicy = null, bool supportsItemCurrency = true)
        {
            await using var historyTransaction = updateCategory
                ? await Context.Database.BeginTransactionAsync(cancellationToken) : null;
            var purchase = await WithItems(Context.Purchases)
                .SingleOrDefaultAsync(x => x.Id == purchaseId, cancellationToken);
            if (purchase?.Items.FirstOrDefault(item => item.Id == itemId) is not { } item)
                return PurchaseMutationResult.NotFound;
            if (purchase.Revision != expectedRevision)
                return PurchaseMutationResult.Conflict;

            var fiscal = IsFiscal(Context, purchase);
            var validation = ResolveItemCurrency(purchase, item, currency, currencyPolicy, supportsItemCurrency,
                fiscal, out var storedCurrency);
            if (validation != PurchaseMutationResult.Applied)
                return validation;
            var (unitPrice, sum) = item.Quantity != quantity || item.Price != price
                ? purchase.CalculateManualItemAmounts(quantity, price, storedCurrency)
                : (item.Price, item.Sum);
            var amountChanged = item.Quantity != quantity || item.Price != unitPrice;
            var currencyChanged = storedCurrency != item.Currency;
            var lineChanged = item.Name != name || amountChanged || currencyChanged;
            if (!lineChanged && (!updateCategory ||
                categoryId.HasValue && item.CategoryState == ItemCategoryState.Confirmed && item.BudgetCategoryId == categoryId))
                return PurchaseMutationResult.Unchanged;

            if (lineChanged && fiscal)
                PreserveOriginalReceipt(purchase);
            if (!updateCategory && NormalizeCategoryKeyPart(item.Name) != NormalizeCategoryKeyPart(name))
                ResetAutomaticCategory(item);
            item.Name = name;
            if (amountChanged || currencyChanged)
            {
                item.Quantity = quantity;
                item.Price = unitPrice;
                item.Sum = sum;
                item.Currency = storedCurrency;
            }
            if (updateCategory)
                await ConfirmCategoryAsync(purchase, item, categoryId, cancellationToken);
            purchase.Revision = checked(purchase.Revision + 1);
            await Context.SaveChangesAsync(cancellationToken);
            if (historyTransaction is not null)
                await historyTransaction.CommitAsync(cancellationToken);
            return PurchaseMutationResult.Applied;
        }

        public async Task<bool> SearchByRawData(byte[]? query)
        {
            if (query is not { Length: > 0 and <= 100 })
                return false;

            var padded = PadRawData(query);
            return await Context.Purchases.AnyAsync(x => x.RawData == padded);
        }

        public async Task<PurchaseMutationResult> AddPurchaseItemAsync(Guid purchaseId, long expectedRevision,
            string name, double quantity, decimal price, CancellationToken cancellationToken = default,
            Guid? categoryId = null, bool updateCategory = false,
            string? currency = null, ManualCurrencySettings? currencyPolicy = null, bool supportsItemCurrency = true)
        {
            await using var historyTransaction = updateCategory
                ? await Context.Database.BeginTransactionAsync(cancellationToken) : null;
            var purchase = await WithItems(Context.Purchases)
                .SingleOrDefaultAsync(x => x.Id == purchaseId, cancellationToken);
            if (purchase is null)
                return PurchaseMutationResult.NotFound;
            if (purchase.Revision != expectedRevision)
                return PurchaseMutationResult.Conflict;

            var fiscal = IsFiscal(Context, purchase);
            var validation = ResolveItemCurrency(purchase, null, currency, currencyPolicy, supportsItemCurrency,
                fiscal, out var storedCurrency);
            if (validation != PurchaseMutationResult.Applied)
                return validation;
            storedCurrency ??= ManualCurrencySettings.NormalizeCode(fiscal ? FiscalCurrency(purchase) : purchase.Currency);
            if (storedCurrency is null && !fiscal && supportsItemCurrency)
                storedCurrency = (currencyPolicy ?? ManualCurrencySettings.Default).DefaultCurrency;
            if (fiscal)
                PreserveOriginalReceipt(purchase);
            var (unitPrice, sum) = purchase.CalculateManualItemAmounts(quantity, price, storedCurrency);
            var lineNumber = purchase.Items.Select(item =>
                Context.Entry(item).Property<int?>(CastarDbContext.LineNumber).CurrentValue ?? -1)
                .DefaultIfEmpty(-1).Max() + 1;
            var item = new Item
            {
                PurchaseId = purchase.Id, Name = name, Quantity = quantity, Price = unitPrice, Sum = sum,
                Currency = storedCurrency
            };
            purchase.Items.Add(item);
            Context.Entry(item).State = EntityState.Added;
            Context.Entry(item).Property<int?>(CastarDbContext.LineNumber).CurrentValue = lineNumber;
            if (updateCategory)
                await ConfirmCategoryAsync(purchase, item, categoryId, cancellationToken);
            purchase.Revision = checked(purchase.Revision + 1);
            await Context.SaveChangesAsync(cancellationToken);
            if (historyTransaction is not null)
                await historyTransaction.CommitAsync(cancellationToken);
            return PurchaseMutationResult.Applied;
        }

        private static PurchaseMutationResult ResolveItemCurrency(Purchase purchase, Item? item, string? input,
            ManualCurrencySettings? policy, bool supportsItemCurrency, bool fiscal, out string? storedCurrency)
        {
            storedCurrency = item?.Currency;
            if (!supportsItemCurrency && (input is not null || purchase.HasMultipleCurrencies()))
                return PurchaseMutationResult.UnsupportedItemCurrency;
            if (input is null)
                return PurchaseMutationResult.Applied;

            var code = ManualCurrencySettings.NormalizeCode(input);
            if (code is null)
                return PurchaseMutationResult.InvalidCurrency;
            if (fiscal)
            {
                if (code != FiscalCurrency(purchase))
                    return PurchaseMutationResult.FiscalCurrency;
                storedCurrency = item is null ? code : storedCurrency;
                return PurchaseMutationResult.Applied;
            }
            if (code != (item is null ? purchase.Currency : item.Currency) && !(policy ?? ManualCurrencySettings.Default).Allows(code))
                return PurchaseMutationResult.InvalidCurrency;
            if (item is null || code != item.Currency)
                storedCurrency = code;
            return PurchaseMutationResult.Applied;
        }

        private static IQueryable<Purchase> WithDetails(IQueryable<Purchase> purchases) =>
            WithItems(purchases.AsNoTrackingWithIdentityResolution().Include(x => x.User));

        private static IQueryable<Purchase> WithItems(IQueryable<Purchase> purchases) =>
            purchases.Include(x => x.Adjustment).Include(x => x.Items.OrderBy(i => EF.Property<int?>(i, CastarDbContext.LineNumber))
                .ThenBy(i => i.CreatedOn).ThenBy(i => i.Id));

        private void TrackNewPurchase(Purchase purchase)
        {
            if (purchase.MessageId != 0)
                purchase.TelegramOriginUserId ??= purchase.UserId;
            var currency = ManualCurrencySettings.NormalizeCode(purchase.Currency);
            Context.Entry(purchase).State = EntityState.Added;
            if (purchase.Adjustment is { } adjustment)
            {
                adjustment.PurchaseId = purchase.Id;
                Context.Entry(adjustment).State = EntityState.Added;
            }
            var index = 0;
            foreach (var item in purchase.Items)
            {
                item.PurchaseId = purchase.Id;
                item.Purchase = purchase;
                item.Currency ??= currency;
                Context.Entry(item).State = EntityState.Added;
                Context.Entry(item).Property<int?>(CastarDbContext.LineNumber).CurrentValue = index++;
            }
        }

        internal static void ThrowIfFiscal(CastarDbContext db, Purchase purchase)
        {
            if (IsFiscal(db, purchase))
                throw new InvalidOperationException("Позиции фискального чека нельзя редактировать вручную.");
        }

        private static bool IsFiscal(CastarDbContext db, Purchase purchase) =>
            purchase.RawData is not null || db.Entry(purchase).Property<byte[]>(CastarDbContext.ReceiptKey).CurrentValue is not null;

        private static string? FiscalCurrency(Purchase purchase) =>
            FiscalReceiptSnapshot.Read(purchase.OriginalReceiptSnapshot)?.Currency ?? purchase.Currency;

        private static void PreserveOriginalReceipt(Purchase purchase)
        {
            purchase.OriginalReceiptSnapshot ??= FiscalReceiptSnapshot.Capture(purchase);
        }

        private static string? DecodeLegacySource(byte[]? rawData)
        {
            if (rawData is not { Length: > 0 })
                return null;
            try
            {
                var decoded = StrictUtf8.GetString(rawData.AsSpan().TrimEnd((byte)0));
                return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
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
