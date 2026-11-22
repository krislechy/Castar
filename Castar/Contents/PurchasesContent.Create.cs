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
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await ConfirmAndApplyAsync("Добавить покупку", editor, "Добавить",
                        async request =>
                        {
                            if (editor.SelectedFrequency is null)
                                return await server.CreatePurchaseAsync(editor.Result!, request);
                            await server.SaveRecurringPurchaseAsync(CreateRecurringDraft(editor), null, request);
                            return true;
                        }, token, editor.Validate, source: sender,
                        editorIcon: (Geometry)FindResource("Lucide.shopping-cart"));
                    return;
                }
                catch (HttpRequestException error) when (!token.IsCancellationRequested &&
                    editor.SelectedFrequency.HasValue && error.StatusCode is HttpStatusCode.BadRequest
                    or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.UnprocessableEntity)
                {
                    await ShowPurchaseActionMessageAsync(error.StatusCode is HttpStatusCode.NotFound
                        or HttpStatusCode.MethodNotAllowed
                        ? "Обновите Castar Server для работы с подписками."
                        : CastarServerClient.DescribeError(error));
                }
            }
        });
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
            if (recurring is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(recurring.Schedule.TimeZoneId, out zone))
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
        }, editor.SelectedFrequency!.Value, editor.ScheduleZone.Id);
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
