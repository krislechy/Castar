using Castar.Api;
using Castar.Compontents;
using Castar.ServerConnection;
using Castar.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Castar.Contents;

public partial class PurchasesContent
{
    private static readonly DependencyPropertyKey PendingPurchaseItemCountPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PendingPurchaseItemCount), typeof(int), typeof(PurchasesContent), new PropertyMetadata(0));
    public static readonly DependencyProperty PendingPurchaseItemCountProperty = PendingPurchaseItemCountPropertyKey.DependencyProperty;
    public int PendingPurchaseItemCount => (int)GetValue(PendingPurchaseItemCountProperty);

    private static readonly DependencyPropertyKey IsPurchaseItemQueueWaitingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsPurchaseItemQueueWaiting), typeof(bool), typeof(PurchasesContent), new PropertyMetadata(false));
    public static readonly DependencyProperty IsPurchaseItemQueueWaitingProperty = IsPurchaseItemQueueWaitingPropertyKey.DependencyProperty;
    public bool IsPurchaseItemQueueWaiting => (bool)GetValue(IsPurchaseItemQueueWaitingProperty);

    private static readonly DependencyPropertyKey PurchaseItemQueueMessagePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PurchaseItemQueueMessage), typeof(string), typeof(PurchasesContent), new PropertyMetadata(""));
    public static readonly DependencyProperty PurchaseItemQueueMessageProperty = PurchaseItemQueueMessagePropertyKey.DependencyProperty;
    public string PurchaseItemQueueMessage => (string)GetValue(PurchaseItemQueueMessageProperty);

    private readonly record struct PurchaseQueueKey(string Scope, Guid PurchaseId);
    private sealed record QueuedPurchaseItem(Guid Id, string Scope, Guid PurchaseId, string? Currency,
        PurchaseItemWriteDto Item, bool NeedsReview = false)
    {
        [JsonIgnore]
        public PurchaseQueueKey Key => new(Scope, PurchaseId);
    }

    private static string PurchaseItemQueuePath => Path.Combine(ClientSettings.DataDirectory, "PurchaseItemQueue.json");
    private QueuedPurchaseItem[]? pendingPurchaseItems;
    private readonly Dictionary<PurchaseQueueKey, string> purchaseItemQueueErrors = [];
    private bool processingPurchaseItemQueue;
    private PurchaseQueueKey? sendingPurchaseItem;

    private string PurchaseItemQueueScope => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(settings.ServerUrl + "\n" + settings.ApiKey)));

    private QueuedPurchaseItem[] ReadPurchaseItemQueue() => pendingPurchaseItems ??=
        File.Exists(PurchaseItemQueuePath)
            ? JsonSerializer.Deserialize<QueuedPurchaseItem[]>(File.ReadAllText(PurchaseItemQueuePath))
                ?? throw new JsonException("Пустая очередь добавления товаров.")
            : [];

    private void SavePurchaseItemQueue(QueuedPurchaseItem[] updated)
    {
        var path = PurchaseItemQueuePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
        File.Move(temporary, path, overwrite: true);
        pendingPurchaseItems = updated;
    }

    private ValidatePopupDialog EnqueuePurchaseItem(string scope, Guid purchaseId, string? currency, PurchaseItemWriteDto item)
    {
        try
        {
            var queue = ReadPurchaseItemQueue();
            SavePurchaseItemQueue([..queue, new(Guid.NewGuid(), scope, purchaseId, currency, item)]);
            UpdatePurchaseItemQueueStatus();
            return new(true, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(false, "Не удалось сохранить очередь товаров на этом компьютере. " + error.Message);
        }
    }

    internal void ResumePurchaseItemQueue()
    {
        var scope = PurchaseItemQueueScope;
        foreach (var key in purchaseItemQueueErrors.Keys.Where(key => key.Scope == scope).ToArray())
            purchaseItemQueueErrors.Remove(key);
        _ = ProcessPurchaseItemQueueAsync();
    }

    private async Task ProcessPurchaseItemQueueAsync()
    {
        if (processingPurchaseItemQueue)
            return;
        processingPurchaseItemQueue = true;
        var refresh = false;
        try
        {
            while (true)
            {
                var scope = PurchaseItemQueueScope;
                var next = ReadPurchaseItemQueue().Where(item => item.Scope == scope)
                    .DistinctBy(item => item.Key)
                    .FirstOrDefault(item => !item.NeedsReview && !purchaseItemQueueErrors.ContainsKey(item.Key));
                if (next is null)
                    return;

                sendingPurchaseItem = next.Key;
                UpdatePurchaseItemQueueStatus();
                try
                {
                    if (await SendQueuedPurchaseItemAsync(next))
                        refresh = true;
                }
                catch (Exception error) when (CastarServerClient.IsConnectionError(error)
                    || error is IOException or UnauthorizedAccessException)
                {
                    purchaseItemQueueErrors[next.Key] = CastarServerClient.IsConnectionError(error)
                        ? CastarServerClient.DescribeError(error) : "Не удалось обновить локальную очередь товаров.";
                    refresh |= ReadPurchaseItemQueue().Any(item => item.Id == next.Id && item.NeedsReview);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (listeningForChanges)
                await ShowPurchaseActionMessageAsync("Не удалось прочитать локальную очередь товаров. " + error.Message);
        }
        finally
        {
            processingPurchaseItemQueue = false;
            sendingPurchaseItem = null;
            UpdatePurchaseItemQueueStatus();
            if (refresh)
                _ = RefreshAfterServerChangeAsync();
        }
    }

    private async Task<bool> SendQueuedPurchaseItemAsync(QueuedPurchaseItem item)
    {
        long? rejectedRevision = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (item.Scope != PurchaseItemQueueScope)
                return false;
            var purchase = await server.GetPurchaseDetailsAsync(item.PurchaseId, CancellationToken.None);
            if (item.Scope != PurchaseItemQueueScope)
                return false;
            if (!string.Equals(purchase.Currency, item.Currency, StringComparison.OrdinalIgnoreCase))
            {
                purchaseItemQueueErrors[item.Key] = "Валюта покупки изменилась. Товар сохранён в очереди.";
                return false;
            }
            if (purchase.Revision is not { } revision || revision == rejectedRevision)
                break;

            // If the app or connection stops during POST, the older API cannot prove whether it committed.
            ReplaceQueuedPurchaseItem(item, item with { NeedsReview = true });
            bool added;
            try
            {
                added = await server.AddPurchaseItemAsync(item.PurchaseId, revision, item.Item, CancellationToken.None);
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            {
                ReplaceQueuedPurchaseItem(item, item);
                rejectedRevision = revision;
                await Task.Delay(150 * (attempt + 1));
                continue;
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
            {
                ReplaceQueuedPurchaseItem(item, item);
                throw;
            }
            ReplaceQueuedPurchaseItem(item, added ? null : item);
            if (!added)
                purchaseItemQueueErrors[item.Key] = "Покупка больше недоступна. Товар сохранён в очереди.";
            return added;
        }

        purchaseItemQueueErrors[item.Key] = "Не удалось получить актуальную ревизию покупки. Товар сохранён в очереди.";
        return false;
    }

    private void ReplaceQueuedPurchaseItem(QueuedPurchaseItem item, QueuedPurchaseItem? replacement) =>
        SavePurchaseItemQueue(ReadPurchaseItemQueue()
            .Select(pending => pending.Id == item.Id ? replacement : pending).OfType<QueuedPurchaseItem>().ToArray());

    private void UpdatePurchaseItemQueueStatus()
    {
        var scope = PurchaseItemQueueScope;
        var purchaseId = (PurchasesList.SelectedItem as PurchaseRow)?.Id;
        var items = pendingPurchaseItems?.Where(item => item.Scope == scope && item.PurchaseId == purchaseId).ToArray();
        var first = items?.FirstOrDefault();
        var error = first is null ? null : purchaseItemQueueErrors.GetValueOrDefault(first.Key);
        var waiting = first is not null && (sendingPurchaseItem == first.Key
            || processingPurchaseItemQueue && !first.NeedsReview && error is null);
        SetValue(PendingPurchaseItemCountPropertyKey, items?.Length ?? 0);
        SetValue(IsPurchaseItemQueueWaitingPropertyKey, waiting);
        SetValue(PurchaseItemQueueMessagePropertyKey, waiting
            ? "Добавление товаров. Ожидаем подтверждения сервера."
            : first?.NeedsReview == true ? "Сервер мог сохранить товар. Нажмите, чтобы проверить очередь."
            : error is not null ? error + " Нажмите, чтобы повторить." : "Товары ожидают добавления.");
    }

    private async void RetryPendingPurchaseItems_Click(object sender, RoutedEventArgs e)
    {
        if (processingPurchaseItemQueue || PurchasesList.SelectedItem is not PurchaseRow row || pendingPurchaseItems is null)
            return;
        var scope = PurchaseItemQueueScope;
        if (pendingPurchaseItems.FirstOrDefault(item => item.Scope == scope && item.PurchaseId == row.Id) is not { } first)
            return;
        if (first.NeedsReview)
        {
            var answer = await PopupDialog.Show(
                $"Сервер мог сохранить «{first.Item.Name}». Проверьте список товаров. Если строка уже есть, уберите её из очереди.",
                PopupDialog.TypeDialogEnum.Confirm, options =>
                {
                    options.Title = "Проверить добавление товара";
                    options.SuccessButton.SuccessConfirmButtonText = "Добавить повторно";
                    options.CancelButton.CancelConfirmButtonText = "Убрать из очереди";
                    options.AllowMissClick = false;
                    options.EscapeReturnsNull = true;
                    return options;
                });
            if (answer is null || scope != PurchaseItemQueueScope)
                return;
            try
            {
                ReplaceQueuedPurchaseItem(first, answer == true ? first with { NeedsReview = false } : null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                await ShowPurchaseActionMessageAsync("Не удалось обновить локальную очередь товаров. " + error.Message);
                return;
            }
        }
        purchaseItemQueueErrors.Remove(first.Key);
        await ProcessPurchaseItemQueueAsync();
    }
}
