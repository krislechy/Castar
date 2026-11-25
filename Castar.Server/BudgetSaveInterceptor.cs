using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Castar.Server;

internal sealed class BudgetSaveInterceptor(BudgetEventPublisher publisher) : SaveChangesInterceptor
{
    private (BudgetChange Purchases, BudgetChange Incomes) pending;

    public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
    {
        pending = result.HasResult ? default : CaptureChanges(data.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        pending = result.HasResult ? default : CaptureChanges(data.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData data, int result)
    {
        PublishChanges(result);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData data, int result, CancellationToken cancellationToken = default)
    {
        PublishChanges(result);
        return ValueTask.FromResult(result);
    }

    private static (BudgetChange Purchases, BudgetChange Incomes) CaptureChanges(DbContext? context)
    {
        // Repository writes commit within SaveChanges. An outer transaction can still roll back.
        if (context is null || context.Database.CurrentTransaction is not null)
            return default;

        var purchases = default(BudgetChange);
        var incomes = default(BudgetChange);
        var entries = context.ChangeTracker.Entries().ToArray();
        var trackedPurchases = entries.Where(entry => entry.Entity is Purchase)
            .ToDictionary(entry => (Guid)entry.Property(nameof(Purchase.Id)).CurrentValue!);
        foreach (var entry in entries)
        {
            switch (entry.Entity, entry.State)
            {
                case (Purchase purchase, EntityState.Added):
                    purchases = purchases.AddCreation(purchase.Id, purchase.AddedOn);
                    break;
                case (Purchase, EntityState.Modified or EntityState.Deleted):
                    purchases = purchases.AddChange(PurchaseDate(entry));
                    break;
                case (Item or PurchaseAdjustment, EntityState.Added or EntityState.Modified or EntityState.Deleted):
                    var owner = entry.Property(nameof(Item.PurchaseId));
                    if (entry.State is not EntityState.Added)
                        purchases = purchases.AddChange(TrackedPurchaseDate(owner.OriginalValue, trackedPurchases));
                    if (entry.State is not EntityState.Deleted && (entry.State == EntityState.Added ||
                        !Equals(owner.OriginalValue, owner.CurrentValue)))
                        purchases = purchases.AddChange(TrackedPurchaseDate(owner.CurrentValue, trackedPurchases));
                    break;
                case (Income income, EntityState.Added):
                    incomes = incomes.AddCreation(income.Id, income.AddedOn);
                    break;
                case (Income, EntityState.Modified or EntityState.Deleted):
                    incomes = incomes.AddChange(EarliestDate(entry.Property(nameof(Income.AddedOn)), entry.State));
                    break;
            }
        }
        return (purchases, incomes);
    }

    private static DateTime? TrackedPurchaseDate(object? key, Dictionary<Guid, EntityEntry> purchases) =>
        key is Guid id && purchases.TryGetValue(id, out var entry) ? PurchaseDate(entry) : null;

    private static DateTime? PurchaseDate(EntityEntry entry) =>
        EarliestDate(entry.Property(nameof(Purchase.AddedOn)), entry.State);

    private static DateTime? EarliestDate(PropertyEntry date, EntityState state)
    {
        var before = date.OriginalValue as DateTime?;
        var after = date.CurrentValue as DateTime?;
        return state switch
        {
            EntityState.Added => after,
            EntityState.Deleted => before,
            _ when before is { } first && after is { } second => first < second ? first : second,
            _ => null
        };
    }

    private void PublishChanges(int saved)
    {
        if (saved > 0)
            publisher.Queue(pending.Purchases, pending.Incomes);
        pending = default;
    }
}
