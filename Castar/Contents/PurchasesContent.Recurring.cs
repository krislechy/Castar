using Castar.Api;
using Castar.Compontents;
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
    private Func<Task>? refreshRecurringPurchases;

    internal void RefreshRecurringPurchasesAfterServerChange()
    {
        if (refreshRecurringPurchases is { } refresh)
            _ = refresh();
    }

    private async void RecurringPurchases_Click(object sender, RoutedEventArgs e)
    {
        await RunPurchaseActionAsync(async token =>
        {
            var panel = new RecurringPurchasesPanel(settings.DisplayTimeZone);
            using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
            var request = session.Token;
            var busy = true;
            var refreshNeeded = false;

            async Task RefreshAsync()
            {
                if (request.IsCancellationRequested)
                    return;
                refreshNeeded = true;
                if (busy)
                    return;

                busy = true;
                try
                {
                    while (refreshNeeded && !request.IsCancellationRequested)
                    {
                        refreshNeeded = false;
                        var result = await server.GetRecurringPurchasesAsync(request);
                        if (!request.IsCancellationRequested)
                            panel.SetRows(result);
                    }
                }
                catch (Exception error) when (CastarServerClient.IsConnectionError(error))
                {
                    if (!request.IsCancellationRequested)
                        panel.SetError(CastarServerClient.DescribeError(error));
                }
                finally
                {
                    busy = false;
                }
            }

            async void ActionRequested(RecurringPurchaseAction action, RecurringPurchaseDto? schedule)
            {
                if (busy || request.IsCancellationRequested)
                    return;

                busy = true;
                panel.SetBusy(true);
                panel.SetError(null);
                try
                {
                    refreshNeeded |= await ApplyRecurringPurchaseActionAsync(panel, action, schedule, request);
                }
                catch (HttpRequestException error) when (!request.IsCancellationRequested &&
                    error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
                {
                    refreshNeeded = true;
                    panel.SetError("Подписка изменилась. Проверьте обновлённый список.");
                }
                catch (Exception error) when (CastarServerClient.IsConnectionError(error))
                {
                    if (!request.IsCancellationRequested)
                        panel.SetError(CastarServerClient.DescribeError(error));
                }
                finally
                {
                    busy = false;
                    if (!request.IsCancellationRequested)
                    {
                        if (refreshNeeded)
                            await RefreshAsync();
                        if (!request.IsCancellationRequested)
                            panel.SetBusy(false);
                    }
                }
            }

            panel.ActionRequested += ActionRequested;
            refreshRecurringPurchases = RefreshAsync;
            try
            {
                try
                {
                    await ButtonLoading.RunAsync(sender, async () =>
                        panel.SetRows(await server.GetRecurringPurchasesAsync(request)));
                }
                catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound
                    or HttpStatusCode.MethodNotAllowed)
                {
                    await ShowPurchaseActionMessageAsync("Обновите Castar Server для работы с подписками.");
                    return;
                }
                finally
                {
                    busy = false;
                }

                if (refreshNeeded)
                    await RefreshAsync();
                request.ThrowIfCancellationRequested();
                await PopupDialog.Show(panel, PopupDialog.TypeDialogEnum.Default, options =>
                {
                    options.Title = "Подписки";
                    options.EditorIcon = (Geometry)FindResource("Lucide.repeat");
                    options.DefaultButton.DefaultButtonText = "Закрыть";
                    return options;
                });
            }
            finally
            {
                refreshRecurringPurchases = null;
                panel.ActionRequested -= ActionRequested;
                await session.CancelAsync();
            }
        });
    }

    private async Task<bool> ApplyRecurringPurchaseActionAsync(RecurringPurchasesPanel panel,
        RecurringPurchaseAction action, RecurringPurchaseDto? schedule, CancellationToken token)
    {
        if (action is RecurringPurchaseAction.Add or RecurringPurchaseAction.Edit)
        {
            var (editor, error) = await LoadManualPurchaseEditorAsync(null, token, schedule, startRecurring: true);
            if (editor is null)
            {
                panel.SetError(error);
                return false;
            }

            var saved = await PopupDialog.ShowNested(panel, editor, options =>
            {
                options.Title = schedule is null ? "Добавить подписку" : "Изменить подписку";
                options.EditorIcon = (Geometry)FindResource("Lucide.repeat");
                options.SuccessButton.SuccessConfirmButtonText = "Сохранить";
                options.AllowMissClick = false;
                options.ConfirmAsync = () => SaveManualPurchaseAsync(editor, schedule, token);
                return options;
            }, editor.Validate);
            return saved == true && !token.IsCancellationRequested;
        }

        if (schedule is null)
            return false;

        if (action is RecurringPurchaseAction.Delete or RecurringPurchaseAction.Skip)
        {
            var delete = action == RecurringPurchaseAction.Delete;
            var confirmation = delete
                ? $"Удалить подписку «{schedule.Schedule.Template.Purchase.Name}»? Уже созданные покупки останутся в расходах."
                : "Пропустить ближайшую покупку по этой подписке?";
            var accepted = await PopupDialog.ShowNested(panel, confirmation, options =>
            {
                options.Title = delete ? "Удалить подписку" : "Пропустить покупку";
                options.SuccessButton.SuccessConfirmButtonText = delete ? "Удалить" : "Пропустить";
                if (delete)
                    options.SuccessButton.SuccessConfirmButtonForeground = Brushes.OrangeRed;
                options.AllowMissClick = false;
                return options;
            });
            if (accepted != true || token.IsCancellationRequested)
                return false;
        }

        if (action == RecurringPurchaseAction.Delete)
            await server.DeleteRecurringPurchaseAsync(schedule, token);
        else
            await server.ChangeRecurringPurchaseAsync(schedule, action switch
            {
                RecurringPurchaseAction.Pause => "pause",
                RecurringPurchaseAction.Resume => "resume",
                RecurringPurchaseAction.Skip => "skip",
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            }, token);
        return true;
    }
}
