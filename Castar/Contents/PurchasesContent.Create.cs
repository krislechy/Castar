using Castar.Compontents;
using Castar.Domain.Models.DataBase;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace Castar.Contents;

public partial class PurchasesContent
{
    private async void AddPurchase_Click(object sender, RoutedEventArgs e)
    {
        await RunPurchaseActionAsync(async token =>
        {
            ManualPurchaseEditor? editor = null;
            string? message = null;
            await ButtonLoading.RunAsync(sender, async () =>
            {
                var configuration = server.GetServerSettingsAsync(token);
                var users = server.GetUsersAsync(token);
                await Task.WhenAll(configuration, users);
                token.ThrowIfCancellationRequested();
                if ((await configuration).ManualCurrency is not { } currencies)
                {
                    message = "Обновите Castar Server для ручного добавления покупки.";
                    return;
                }
                if ((await users).Count == 0)
                {
                    message = "Добавьте пользователя в настройках.";
                    return;
                }
                editor = new ManualPurchaseEditor(BudgetTime.InZone(DateTime.UtcNow, settings.DisplayTimeZone),
                    currencies, await users);
            });
            if (message is not null)
            {
                await ShowPurchaseActionMessageAsync(message);
                return;
            }
            if (editor is null || token.IsCancellationRequested)
                return;
            await ConfirmAndApplyAsync("Добавить покупку", editor, "Добавить",
                request => server.CreatePurchaseAsync(editor.Result!, request), token, editor.Validate, source: sender);
        });
    }

    private async void AddPurchaseItem_Click(object sender, RoutedEventArgs e)
    {
        if (PurchasesList.SelectedItem is not PurchaseRow { Source.Revision: { } revision } row)
            return;
        var editor = new PurchaseItemEditor(new(Guid.Empty, "", null, 1, 0, 0), row.Source.Currency);
        await ConfirmMutationAsync("Добавить товар", editor, "Добавить",
            token => server.AddPurchaseItemAsync(row.Id, revision, editor.Result!, token), validate: editor.Validate, source: sender);
    }
}
