using Castar.Compontents;
using Castar.Api;
using Castar.Domain.Models.DataBase;
using Castar.ServerConnection;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Castar.Contents;

public partial class PurchasesContent
{
    private async void AddPurchase_Click(object sender, RoutedEventArgs e)
    {
        await RunPurchaseActionAsync(async token =>
        {
            var (editor, message) = await LoadManualPurchaseEditorAsync(sender, token);
            if (message is not null)
            {
                await ShowPurchaseActionMessageAsync(message);
                return;
            }
            if (editor is null || token.IsCancellationRequested)
                return;
            var accepted = await PopupDialog.Show(editor, PopupDialog.TypeDialogEnum.Confirm, options =>
            {
                options.Title = "Добавить покупку";
                options.EditorIcon = (Geometry)FindResource("Lucide.shopping-cart");
                options.SuccessButton.SuccessConfirmButtonText = "Добавить";
                options.AllowMissClick = false;
                options.ConfirmAsync = () => SaveManualPurchaseAsync(editor, null, token);
                return options;
            }, editor.Validate);
            if (accepted == true && !token.IsCancellationRequested)
                await RefreshAfterPurchaseMutationAsync(token);
        });
    }

    private async Task<ValidatePopupDialog> SaveManualPurchaseAsync(ManualPurchaseEditor editor,
        RecurringPurchaseDto? schedule, CancellationToken token)
    {
        var isRecurring = editor.SelectedFrequency.HasValue;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!isRecurring)
                return await server.CreatePurchaseAsync(editor.Result!, token)
                    ? new(true, null) : new(false, "Сервер не смог добавить покупку.");

            await server.SaveRecurringPurchaseAsync(CreateRecurringDraft(editor), schedule, token);
            return new(true, null);
        }
        catch (HttpRequestException error) when (isRecurring &&
            (error.StatusCode == HttpStatusCode.MethodNotAllowed ||
             error.StatusCode == HttpStatusCode.NotFound && schedule is null))
        {
            return new(false, "Обновите Castar Server для работы с подписками.");
        }
        catch (HttpRequestException error) when (schedule is not null &&
            error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return new(false, "Подписка изменилась. Откройте её заново для редактирования.");
        }
        catch (Exception error) when (CastarServerClient.IsConnectionError(error))
        {
            return new(false, CastarServerClient.DescribeError(error));
        }
    }

    private async Task<(ManualPurchaseEditor? Editor, string? Error)> LoadManualPurchaseEditorAsync(
        object? source, CancellationToken token, RecurringPurchaseDto? recurring = null, bool startRecurring = false)
    {
        ManualPurchaseEditor? editor = null;
        string? error = null;
        await ButtonLoading.RunAsync(source, async () =>
        {
            var configuration = server.GetServerSettingsAsync(token);
            var users = server.GetUsersAsync(token);
            var categories = server.GetOperationCategoriesAsync(BudgetEvents.Purchase, token);
            await Task.WhenAll(configuration, users, categories);
            token.ThrowIfCancellationRequested();
            if ((await configuration).ManualCurrency is not { } currencies)
            {
                error = "Обновите Castar Server для ручного добавления покупки.";
                return;
            }
            if ((await users).Count == 0)
            {
                error = "Добавьте пользователя в настройках.";
                return;
            }

            var zone = settings.DisplayTimeZone;
            if (recurring is not null && !RecurringPurchaseTime.TryResolve(recurring.Schedule, out zone))
            {
                error = "Часовой пояс подписки недоступен.";
                return;
            }
            editor = new ManualPurchaseEditor(BudgetTime.InZone(DateTime.UtcNow, zone),
                currencies, await users, await categories, recurring, zone, startRecurring);
        });
        return (editor, error);
    }

    private static RecurringPurchaseWriteDto CreateRecurringDraft(ManualPurchaseEditor editor)
    {
        var draft = editor.Result!;
        return new(draft with
        {
            Purchase = draft.Purchase with
            {
                AddedOn = DateTime.SpecifyKind(draft.Purchase.AddedOn, DateTimeKind.Unspecified),
                AddedOnUtc = null
            }
        }, editor.SelectedFrequency!.Value, editor.SelectedLegacyTimeZoneId, editor.SelectedUtcOffsetMinutes,
            editor.SelectedZoneRuleId);
    }

    private async void AddPurchaseItem_Click(object sender, RoutedEventArgs e)
    {
        if (PurchasesList.SelectedItem is not PurchaseRow { Source.Revision: not null } row)
            return;
        var scope = PurchaseItemQueueScope;
        await ShowPurchaseItemEditorAsync(new(Guid.Empty, "", null, 1, 0, 0), row.Source.Currency, "Добавить товар", "Добавить",
            item => EnqueuePurchaseItem(scope, row.Id, row.Source.Currency, item), sender);
    }
}
