using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Castar.DAL
{
    public sealed class CastarDbContext(DbContextOptions<CastarDbContext> options) : DbContext(options)
    {
        internal const string ReceiptKey = "ReceiptKey";
        internal const string ReceiptIndex = "UX_Purchases_ReceiptKey";
        internal const string MessageIdField = "_messageId";
        internal const string LineNumber = "LineNumber";

        public DbSet<User> Users => Set<User>();
        public DbSet<Purchase> Purchases => Set<Purchase>();
        public DbSet<Item> Items => Set<Item>();
        public DbSet<Income> Incomes => Set<Income>();
        public DbSet<ExchangeRateEntry> ExchangeRates => Set<ExchangeRateEntry>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var users = modelBuilder.Entity<User>();
            MapTable(users, "Users");
            users.Property(x => x.Name).HasMaxLength(200);
            users.Property(x => x.TelegramNotificationsEnabled).HasDefaultValue(true).HasSentinel(true);
            users.Property(x => x.ColorRaw).HasColumnName("Color");
            users.Property(x => x.ImageRaw).HasColumnName("Image");
            users.Ignore(x => x.Color);
            users.Ignore(x => x.Items);

            var purchases = modelBuilder.Entity<Purchase>();
            MapTable(purchases, "Purchases");
            purchases.Ignore(x => x.MessageId);
            purchases.Property<long?>(MessageIdField).HasColumnName("MessageId");
            purchases.HasIndex(nameof(Purchase.TelegramOriginUserId), MessageIdField)
                .HasDatabaseName("IX_Purchases_TelegramOriginUserId_MessageId");
            purchases.Property(x => x.Name).HasMaxLength(400);
            purchases.Property(x => x.TotalSum).HasPrecision(24, 2);
            purchases.Property(x => x.Revision).HasDefaultValue(0L).IsConcurrencyToken();
            purchases.Property(x => x.AddedOn).IsRequired();
            purchases.Property(x => x.RetailPlace).HasMaxLength(400);
            purchases.Property(x => x.RetailPlaceAddress).HasMaxLength(400);
            purchases.Property(x => x.Currency).HasMaxLength(3);
            purchases.Property<byte[]?>(ReceiptKey).HasMaxLength(32);
            purchases.HasIndex(ReceiptKey).IsUnique().HasDatabaseName(ReceiptIndex)
                .HasFilter("[ReceiptKey] IS NOT NULL");
            purchases.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_Purchases_Users");

            var items = modelBuilder.Entity<Item>();
            MapTable(items, "Items");
            items.Property(x => x.Name).HasMaxLength(400);
            items.Property(x => x.Price).HasPrecision(24, 2);
            items.Property(x => x.Sum).HasPrecision(24, 2);
            items.Property<int?>(LineNumber);
            items.Ignore(x => x.Users);
            items.HasOne(x => x.Purchase).WithMany(x => x.Items).HasForeignKey(x => x.PurchaseId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_Items_Purchases");

            var incomes = modelBuilder.Entity<Income>();
            MapTable(incomes, "Incomes");
            incomes.Ignore(x => x.MessageId);
            incomes.Property<long?>(MessageIdField).HasColumnName("MessageId");
            incomes.Property<int?>(LineNumber);
            incomes.HasIndex(nameof(Income.UserId), MessageIdField).HasDatabaseName("IX_Incomes_UserId_MessageId");
            incomes.Property(x => x.Sum).HasPrecision(24, 2);
            incomes.Property(x => x.Currency).HasMaxLength(3);
            incomes.Property(x => x.Source).HasMaxLength(400);
            incomes.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.NoAction).HasConstraintName("FK_Incomes_Users");

            var rates = modelBuilder.Entity<ExchangeRateEntry>();
            rates.ToTable("ExchangeRates");
            rates.HasKey(x => new { x.Target, x.Source, x.Date });
            rates.Property(x => x.Target).HasMaxLength(3);
            rates.Property(x => x.Source).HasMaxLength(3);
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            StampChanges();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            StampChanges();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        private void StampChanges()
        {
            var now = DateTime.UtcNow;
            foreach (var entry in ChangeTracker.Entries<BaseDbModel>())
            {
                if (entry.State == EntityState.Added)
                    entry.Property(x => x.CreatedOn).CurrentValue = now;
                else if (entry.State == EntityState.Modified)
                    entry.Property(x => x.ModifiedOn).CurrentValue = now;
            }
            foreach (var entry in ChangeTracker.Entries<Purchase>())
            {
                var purchase = entry.Entity;
                if (entry.State == EntityState.Added && purchase.RateDate is null ||
                    entry.Property(x => x.AddedOn).IsModified)
                    purchase.RateDate = purchase.ReceiptDate ??
                        (purchase.AddedOn is { } date ? BudgetTime.BelgradeDate(date) : null);
            }
            foreach (var entry in ChangeTracker.Entries<Income>())
            {
                if (entry.State == EntityState.Added && entry.Entity.RateDate is null ||
                    entry.Property(x => x.AddedOn).IsModified)
                    entry.Entity.RateDate = BudgetTime.BelgradeDate(entry.Entity.AddedOn);
            }
        }

        private static void MapTable<TEntity>(EntityTypeBuilder<TEntity> entity, string table)
            where TEntity : BaseDbModel
        {
            entity.HasKey(x => x.Id);
            entity.ToTable(table);
        }
    }
}
