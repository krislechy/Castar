using Castar.Domain.Models.DataBase;
using Microsoft.EntityFrameworkCore;
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
        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.Entity, entry.State)
            {
                case (Purchase purchase, EntityState.Added):
                    purchases = purchases.AddCreation(purchase.Id, purchase.AddedOn);
                    break;
                case (Purchase or Item, EntityState.Added or EntityState.Modified or EntityState.Deleted):
                    purchases = purchases with { Changed = true };
                    break;
                case (Income income, EntityState.Added):
                    incomes = incomes.AddCreation(income.Id, income.AddedOn);
                    break;
                case (Income, EntityState.Modified or EntityState.Deleted):
                    incomes = incomes with { Changed = true };
                    break;
            }
        }
        return (purchases, incomes);
    }

    private void PublishChanges(int saved)
    {
        if (saved > 0)
            publisher.Queue(pending.Purchases, pending.Incomes);
        pending = default;
    }
}
