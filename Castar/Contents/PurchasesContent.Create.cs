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
            var configuration = server.GetServerSettingsAsync(token);
            var users = server.GetUsersAsync(token);
            await Task.WhenAll(configuration, users);
            if ((await configuration).ManualCurrency is not { } currencies)
            {
                await ShowPurchaseActionMessageAsync("Обновите Castar Server для ручного добавления покупки.");
                return;
            }
            if ((await users).Count == 0)
            {
                await ShowPurchaseActionMessageAsync("Добавьте пользователя в настройках.");
                return;
            }

            var editor = new ManualPurchaseEditor(BudgetTime.InZone(DateTime.UtcNow, settings.DisplayTimeZone),
                currencies, await users);
            await ConfirmAndApplyAsync("Добавить покупку", editor, "Добавить",
                request => server.CreatePurchaseAsync(editor.Result!, request), token, editor.Validate);
        });
    }

    private async void AddPurchaseItem_Click(object sender, RoutedEventArgs e)
    {
        if (PurchasesList.SelectedItem is not PurchaseRow { Source.Revision: { } revision } row)
            return;
        var editor = new PurchaseItemEditor(new(Guid.Empty, "", null, 1, 0, 0), row.Source.Currency);
        await ConfirmMutationAsync("Добавить товар", editor, "Добавить",
            token => server.AddPurchaseItemAsync(row.Id, revision, editor.Result!, token), validate: editor.Validate);
    }
}
