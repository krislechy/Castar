using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Castar.Server;

internal sealed class BudgetSaveInterceptor(BudgetEventPublisher publisher) : SaveChangesInterceptor
{
    private BudgetChanges pending;

    public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
    {
        CaptureChanges(data.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        CaptureChanges(data.Context);
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

    private void CaptureChanges(DbContext? context)
    {
        pending = BudgetChanges.None;
        // Repository writes commit within SaveChanges. An outer transaction can still roll back.
        if (context is null || context.Database.CurrentTransaction is not null)
            return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            pending |= (entry.Entity, entry.State) switch
            {
                (Purchase, EntityState.Added) => BudgetChanges.PurchaseChanged | BudgetChanges.PurchaseCreated,
                (Purchase or Item, EntityState.Added or EntityState.Modified or EntityState.Deleted) => BudgetChanges.PurchaseChanged,
                (Income, EntityState.Added) => BudgetChanges.IncomeChanged | BudgetChanges.IncomeCreated,
                (Income, EntityState.Modified or EntityState.Deleted) => BudgetChanges.IncomeChanged,
                _ => BudgetChanges.None
            };
        }
    }

    private void PublishChanges(int saved)
    {
        if (saved > 0)
            publisher.Queue(pending);
        pending = BudgetChanges.None;
    }
}
