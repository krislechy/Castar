using Castar.Api;
using Castar.Domain.Models.DataBase;
using Castar.Settings;
using Castar.Logger;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Castar.ServerConnection;

public sealed class CastarServerClient(ClientSettings settings) : IDisposable
{
    private readonly ICLogger? logger;

    public CastarServerClient(ClientSettings settings, ICLogger logger) : this(settings) => this.logger = logger;
    internal const string ConnectionNotEstablished = "Соединение с сервером не установлено.";
    private readonly HttpClient httpsClient = CreateHttpClient(useProxy: true);
    private readonly HttpClient localClient = CreateHttpClient(useProxy: false);
    private ConcurrentDictionary<(string Server, string Key, Guid Id), byte[]> avatars = new();
    private long statisticsFinancialRevision;
    internal Func<DateTimeOffset?, Task>? FinancialMutationAsync { get; set; }

    internal void ClearAvatarCache() => Interlocked.Exchange(ref avatars, new());

    // An opaque cache partition; never expose the API credential in keys or diagnostics.
    internal string StatisticsCacheIdentity => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(settings.ServerUrl + "\n" + settings.ProtectedApiKey + "\n"
            + settings.DisplayTimeZone.ToSerializedString())));

    internal string StatisticsCacheScope => StatisticsCacheIdentity + ":" +
        Interlocked.Read(ref statisticsFinancialRevision).ToString(CultureInfo.InvariantCulture);

    internal event EventHandler? ConnectionChanged
    {
        add => settings.ConnectionChanged += value;
        remove => settings.ConnectionChanged -= value;
    }

    private static HttpClient CreateHttpClient(bool useProxy) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip,
        UseProxy = useProxy,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = 32 * 1024 * 1024
    };

    public static Uri ParseServerUrl(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))
            || string.IsNullOrEmpty(uri.Host)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("Укажите HTTPS-адрес Castar Server. Для локального сервера доступен HTTP.");
        }
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal static bool IsConnectionError(Exception error) =>
        error is ArgumentException or HttpRequestException or OperationCanceledException or JsonException;

    internal static string DescribeConnectionFailure(Exception error, Uri? server = null)
    {
        var detail = DescribeError(error, server);
        return detail == ConnectionNotEstablished ? detail : $"{ConnectionNotEstablished} {detail}";
    }

    internal static string DescribeError(Exception error, Uri? server = null) => error switch
    {
        ArgumentException => error.Message,
        HttpRequestException requestError => DescribeRequestError(requestError, server),
        OperationCanceledException { InnerException: TimeoutException } => "Истекло время ожидания ответа сервера.",
        OperationCanceledException => "Запрос отменён.",
        JsonException => "Ответ сервера имеет неверный формат.",
        _ => "Не удалось получить ответ Castar Server. Проверьте адрес и доступность сервера."
    };

    private static string DescribeRequestError(HttpRequestException error, Uri? server)
    {
        var message = error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "Не удалось найти сервер по имени. Проверьте адрес и DNS.",
            HttpRequestError.SecureConnectionError => "Сбой TLS. Проверьте HTTPS-адрес и сертификат сервера.",
            HttpRequestError.ProxyTunnelError => "Прокси не установил соединение с сервером.",
            HttpRequestError.ResponseEnded => "Соединение оборвалось до получения полного ответа.",
            HttpRequestError.InvalidResponse or HttpRequestError.HttpProtocolError or HttpRequestError.VersionNegotiationError =>
                "Сервер вернул некорректный ответ HTTP. Проверьте адрес и протокол.",
            HttpRequestError.ConfigurationLimitExceeded => "Ответ сервера превышает допустимый размер.",
            _ when error.StatusCode is not null => error.Message,
            _ => DescribeTransportError(error, server)
        };

        return error.StatusCode is { } status ? $"{message.TrimEnd('.')} (HTTP {(int)status})." : message;
    }

    private static string DescribeTransportError(HttpRequestException error, Uri? server)
    {
        return (error.GetBaseException() as SocketException)?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused when server is { IsLoopback: true } =>
                $"На порте {server.Port} нет соединения. Проверьте, что Castar Server запущен.",
            SocketError.ConnectionRefused => "Сервер отклонил TCP-подключение. Проверьте адрес и порт.",
            SocketError.TimedOut => "Истекло время ожидания TCP-подключения.",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
                "Сервер недоступен по сети. Проверьте подключение.",
            SocketError.ConnectionReset or SocketError.ConnectionAborted => "TCP-соединение оборвалось.",
            _ when error.HttpRequestError == HttpRequestError.ConnectionError => "Не удалось установить TCP-соединение с сервером.",
            _ => "Не удалось получить ответ Castar Server. Проверьте адрес и доступность сервера."
        };
    }

    public async Task TestConnectionAsync(Uri server, string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, server, apiKey, "api/status");
        using var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var status = await ReadResponseAsync<ServerStatus>(response, cancellationToken);
        if (status.ApiVersion != 1)
            throw new HttpRequestException("Версия API Castar Server не поддерживается.", null, response.StatusCode);
    }

    public async Task<ObservableCollection<User>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await SendAsync<UserDto[]>(HttpMethod.Get, "api/users", cancellationToken: cancellationToken);
        return new ObservableCollection<User>(users.Select(ToUser));
    }

    public async Task<User> AddUserAsync(User user) =>
        ToUser(await SendAsync<UserDto>(HttpMethod.Post, "api/users", UserWrite(user)));

    internal async Task<(User User, bool CanSetTelegramNotifications)> GetUserForEditingAsync(Guid id)
    {
        var user = await SendAsync<UserDto>(HttpMethod.Get, $"api/users/{id:D}");
        return (ToUser(user), user.TelegramNotificationsEnabled.HasValue);
    }

    internal Task<byte[]?> GetUserAvatarAsync(Guid id) => GetUserAvatarAsync(id, CancellationToken.None);

    internal async Task<byte[]?> GetUserAvatarAsync(Guid id, CancellationToken cancellationToken)
    {
        const int maxAvatarSize = 1024 * 1024;
        var cache = Volatile.Read(ref avatars);
        var key = (settings.ServerUrl, settings.ApiKey, id);
        if (cache.TryGetValue(key, out var image))
            return image;
        using var request = CreateRequest(HttpMethod.Get, ParseServerUrl(key.ServerUrl), key.ApiKey, $"api/users/{id:D}/avatar");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await TransportFor(request).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound || response.Content.Headers.ContentLength > maxAvatarSize)
            return null;
        EnsureSuccess(response);
        await response.Content.LoadIntoBufferAsync(maxAvatarSize, timeout.Token);
        image = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        cache.TryAdd(key, image);
        return image;
    }

    internal async Task DownloadDatabaseBackupAsync(string fileName, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, ParseServerUrl(settings.ServerUrl), settings.ApiKey, "api/backup");
        using var response = await TransportFor(request).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new HttpRequestException("Сервер не поддерживает резервное копирование.", null, response.StatusCode);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentType?.MediaType != "application/vnd.sqlite3"
            || response.Content.Headers.ContentLength is not > 0)
            throw new HttpRequestException("Сервер вернул неверный формат резервной копии.");

        var destination = Path.GetFullPath(fileName);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".castar-backup-{Guid.NewGuid():N}.part");
        var created = false;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                created = true;
                await response.Content.CopyToAsync(file, cancellationToken);
                if (file.Length != response.Content.Headers.ContentLength)
                    throw new HttpRequestException("Соединение оборвалось до получения полной резервной копии.");
                await file.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
                File.Delete(temporary);
        }
    }

    public async Task<User> UpdateUserAsync(User user, bool updateTelegramNotifications = false)
    {
        var input = UserWrite(user);
        if (updateTelegramNotifications)
            input = input with { TelegramNotificationsEnabled = user.TelegramNotificationsEnabled };
        return ToUser(await SendAsync<UserDto>(HttpMethod.Put, $"api/users/{user.Id:D}", input));
    }

    public Task<bool> DeleteUserAsync(Guid id) => DeleteAsync($"api/users/{id:D}");

    public Task<ObservableCollection<Income>> GetIncomesAsync(DateTime start, DateTime finish,
        CancellationToken cancellationToken = default) => GetIncomesAsync(start, finish, null, cancellationToken);

    internal async Task<ObservableCollection<Income>> GetIncomesAsync(DateTime start, DateTime finish,
        IReadOnlyDictionary<Guid, byte[]>? previousAvatars, CancellationToken cancellationToken)
    {
        var incomes = await GetIncomeRowsAsync(start, finish, cancellationToken);
        var users = incomes.Select(income => income.User).DistinctBy(user => user.Id).ToArray();
        if (users.Any(user => user.HasAvatar is null))
        {
            var legacyUsers = (await GetUsersAsync(cancellationToken)).ToDictionary(user => user.Id);
            return new ObservableCollection<Income>(incomes.Select(income =>
                ToIncome(income, legacyUsers.GetValueOrDefault(income.UserId))));
        }

        var usersById = users.ToDictionary(user => user.Id, ToUser);
        await Parallel.ForEachAsync(users.Where(user => user.HasAvatar == true),
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 },
            async (user, token) =>
            {
                try { usersById[user.Id].ImageRaw = await GetUserAvatarAsync(user.Id, token); }
                catch (Exception error) when (!token.IsCancellationRequested && IsConnectionError(error))
                {
                    if (error is not HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
                        && previousAvatars?.TryGetValue(user.Id, out var lastImage) == true)
                        usersById[user.Id].ImageRaw = lastImage;
                }
            });
        return new ObservableCollection<Income>(incomes.Select(income => ToIncome(income, usersById[income.UserId])));
    }

    internal Task<IncomeDto[]> GetIncomeRowsAsync(DateTime start, DateTime finish, CancellationToken cancellationToken) =>
        SendAsync<IncomeDto[]>(HttpMethod.Get, $"api/incomes?{DateQuery(start, finish)}", cancellationToken: cancellationToken);

    internal async Task<Income[]> GetIncomeAmountsAsync(DateTime start, DateTime finish, CancellationToken token) =>
        (await GetIncomeRowsAsync(start, finish, token).ConfigureAwait(false)).Select(row => ToIncome(row, null)).ToArray();

    public async Task<PurchaseDto[]> GetPurchasesAsync(DateTime start, DateTime finish, CancellationToken cancellationToken = default)
    {
        var purchases = await SendAsync<PurchaseDto[]>(HttpMethod.Get, $"api/purchases?{DateQuery(start, finish)}",
            cancellationToken: cancellationToken);
        return purchases.Select(DisplayPurchase).ToArray();
    }

    internal async Task<PurchaseDto[]> GetPurchaseSummariesAsync(DateTime start, DateTime finish,
        CancellationToken token, bool requireCategoryCoverage = false)
    {
        PurchaseDto[] summaries;
        try
        {
            summaries = await SendAsync<PurchaseDto[]>(HttpMethod.Get, $"api/purchases/summary?{DateQuery(start, finish)}",
                cancellationToken: token);
        }
        catch (HttpRequestException error) when (MissingRoute(error))
        {
            return await GetPurchasesAsync(start, finish, token);
        }
        if (requireCategoryCoverage && summaries.Any(row => row.ItemsDeferred && row.HasUncategorizedItems is null))
            return await GetPurchasesAsync(start, finish, token);
        return summaries.Select(DisplayPurchase).ToArray();
    }

    internal async Task<PurchaseDto> GetPurchaseDetailsAsync(Guid id, CancellationToken token) =>
        DisplayPurchase(await SendAsync<PurchaseDto>(HttpMethod.Get, $"api/purchases/{id:D}", cancellationToken: token));

    internal async Task<BudgetOperationDto[]> GetBudgetOperationsAsync(DateTime start, DateTime finish, CancellationToken token,
        bool includeOpening = true)
    {
        try
        {
            var includesOpening = false;
            var rows = await SendAsync<BudgetOperationDto[]>(HttpMethod.Get,
                $"api/budget/operations?{DateQuery(start, finish)}&includeOpening={includeOpening.ToString().ToLowerInvariant()}", cancellationToken: token,
                observeResponse: response => includesOpening = response.Headers.TryGetValues("X-Castar-Budget-Opening", out var values)
                    && values.Contains("1"));
            if (!includeOpening || includesOpening || start == DateTime.MinValue)
                return rows;

            // Older servers ignore includeOpening. Their existing lightweight endpoint still supplies history.
            var history = await SendAsync<BudgetOperationDto[]>(HttpMethod.Get,
                $"api/budget/operations?{DateQuery(DateTime.MinValue, finish)}", cancellationToken: token);
            return MarkOpening(history);
        }
        catch (HttpRequestException error) when (MissingRoute(error))
        {
            var historyStart = includeOpening ? DateTime.MinValue : start;
            var incomes = GetIncomeRowsAsync(historyStart, finish, token);
            var purchases = SendAsync<PurchaseDto[]>(HttpMethod.Get, $"api/purchases?{DateQuery(historyStart, finish)}",
                cancellationToken: token);
            await Task.WhenAll(incomes, purchases);
            return MarkOpening((await incomes).Select(row => new BudgetOperationDto(true, row.Currency, row.Sum,
                    row.AddedOnUtc ?? ReadLegacyDate(row.AddedOn), row.RateDate ?? DateOnly.FromDateTime(row.AddedOn)))
                .Concat((await purchases).SelectMany(row =>
                    (row.AmountsByCurrency ?? [new(row.TotalSum, row.Currency)]).Select(amount => new BudgetOperationDto(false, amount.Currency, amount.Amount,
                    row.AddedOnUtc ?? (row.AddedOn is { } date ? ReadLegacyDate(date) : null),
                    row.RateDate ?? row.ReceiptDate ?? (row.AddedOn is { } legacy ? DateOnly.FromDateTime(legacy) : null),
                    row.ReceiptDate)))).ToArray());
        }

        BudgetOperationDto[] MarkOpening(BudgetOperationDto[] rows)
        {
            var first = PeriodBoundary(start, 1);
            return rows.Select(row => row.AddedOnUtc is { } instant && instant.UtcDateTime < first
                ? row with { IsOpening = true, OpeningCount = 1 } : row).ToArray();
        }
    }

    internal Task<BudgetCategoryAmountDto[]> GetBudgetCategoryAmountsAsync(DateTime start, DateTime finish,
        CancellationToken token)
    {
        var period = GetUtcPeriod(start, finish);
        return SendAsync<BudgetCategoryAmountDto[]>(HttpMethod.Get,
            $"api/budget/category-breakdown?{ExclusiveUtcRange(period.StartUtc, period.EndUtc)}", cancellationToken: token);
    }

    private static bool MissingRoute(HttpRequestException error) =>
        error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed;

    private PurchaseDto DisplayPurchase(PurchaseDto purchase) => purchase with
        {
            RateDate = purchase.RateDate ?? purchase.ReceiptDate ??
                (purchase.AddedOn is { } original ? DateOnly.FromDateTime(original) : null),
            AddedOn = DisplayOperationDate(purchase.AddedOn, purchase.AddedOnUtc)
        };

    public Task<bool> EditPurchaseAsync(Guid purchaseId, long revision, PurchaseWriteDto purchase,
        CancellationToken cancellationToken = default) =>
        SendMutationAsync(HttpMethod.Put, FormattableString.Invariant($"api/purchases/{purchaseId:D}?revision={revision}"),
            cancellationToken, WritePurchaseTime(purchase));

    public Task<bool> CreatePurchaseAsync(PurchaseCreateDto draft, CancellationToken cancellationToken) =>
        SendMutationAsync(HttpMethod.Post, "api/purchases", cancellationToken,
            draft with { Purchase = WritePurchaseTime(draft.Purchase) });

    private const string RecurringPurchasesPath = "api/recurring-purchases";

    internal Task<RecurringPurchaseDto[]> GetRecurringPurchasesAsync(CancellationToken token) =>
        SendAsync<RecurringPurchaseDto[]>(HttpMethod.Get, RecurringPurchasesPath, cancellationToken: token);

    internal Task<RecurringPurchaseDto> SaveRecurringPurchaseAsync(RecurringPurchaseWriteDto schedule,
        RecurringPurchaseDto? existing, CancellationToken token) =>
        existing is null
            ? SendAsync<RecurringPurchaseDto>(HttpMethod.Post, RecurringPurchasesPath, schedule, token, readProblemDetails: true)
            : SendAsync<RecurringPurchaseDto>(HttpMethod.Put, RecurringPurchasePath(existing), schedule, token, readProblemDetails: true);

    internal Task<RecurringPurchaseDto> ChangeRecurringPurchaseAsync(RecurringPurchaseDto schedule, string action,
        CancellationToken token) =>
        SendAsync<RecurringPurchaseDto>(HttpMethod.Post, RecurringPurchasePath(schedule, "/" + action),
            cancellationToken: token, readProblemDetails: true);

    internal Task<bool> DeleteRecurringPurchaseAsync(RecurringPurchaseDto schedule, CancellationToken token) =>
        DeleteAsync(RecurringPurchasePath(schedule), token);

    private static string RecurringPurchasePath(RecurringPurchaseDto schedule, string suffix = "") =>
        FormattableString.Invariant($"{RecurringPurchasesPath}/{schedule.Id:D}{suffix}?revision={schedule.Revision}");

    public Task<bool> AddPurchaseItemAsync(Guid purchaseId, long revision, PurchaseItemWriteDto item,
        CancellationToken cancellationToken) =>
        SendMutationAsync(HttpMethod.Post, FormattableString.Invariant($"api/purchases/{purchaseId:D}/items?revision={revision}"),
            cancellationToken, item);

    public Task<bool> EditPurchaseItemAsync(Guid purchaseId, Guid itemId, long revision, PurchaseItemWriteDto item,
        CancellationToken cancellationToken = default) =>
        SendMutationAsync(HttpMethod.Put, FormattableString.Invariant($"api/purchases/{purchaseId:D}/items/{itemId:D}?revision={revision}"),
            cancellationToken, item);

    internal Task<(PurchaseClassificationWorkDto[] Work, int? Remaining)> GetPendingPurchaseClassificationAsync(
        bool includeCount, CancellationToken token, DateTime? startUtc = null, DateTime? endUtc = null) =>
        GetPendingClassificationAsync<PurchaseClassificationWorkDto>(BudgetEvents.Purchase, includeCount, token, startUtc, endUtc);

    internal Task<(IncomeClassificationWorkDto[] Work, int? Remaining)> GetPendingIncomeClassificationAsync(
        bool includeCount, CancellationToken token, DateTime? startUtc = null, DateTime? endUtc = null) =>
        GetPendingClassificationAsync<IncomeClassificationWorkDto>(BudgetEvents.Income, includeCount, token, startUtc, endUtc);

    private async Task<(T[] Work, int? Remaining)> GetPendingClassificationAsync<T>(string kind,
        bool includeCount, CancellationToken token, DateTime? startUtc, DateTime? endUtc)
    {
        int? remaining = null;
        var path = $"{ClassificationPath(kind)}/pending?limit=100";
        if (includeCount)
            path += "&includeCount=true";
        if (startUtc is { } first && endUtc is { } last)
            path += "&" + ExclusiveUtcRange(first, last);
        var work = await SendAsync<T[]>(HttpMethod.Get, path, cancellationToken: token,
            observeResponse: includeCount ? response =>
            {
                if (response.Headers.TryGetValues("X-Castar-Classification-Pending", out var values)
                    && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                    remaining = count;
            } : null).ConfigureAwait(false);
        return (work, remaining);
    }

    internal Task<PurchaseClassificationQueueDto> QueueClassificationAsync(string kind, CancellationToken token,
        DateTime startUtc, DateTime endUtc) => SendAsync<PurchaseClassificationQueueDto>(HttpMethod.Post,
        $"{ClassificationPath(kind)}/queue?{ExclusiveUtcRange(startUtc, endUtc)}",
        cancellationToken: token, readProblemDetails: true);

    public async Task<int> GetClassificationCountAsync(string kind, DateTime startUtc, DateTime endUtc,
        CancellationToken token) => (await SendAsync<ClassificationCountDto>(HttpMethod.Get,
            $"{ClassificationPath(kind)}/count?{ExclusiveUtcRange(startUtc, endUtc)}", cancellationToken: token)
            .ConfigureAwait(false)).Count;

    internal async Task<(int Purchases, int Incomes)?> GetUncategorizedCountsAsync(DateTime start, DateTime finish,
        CancellationToken token)
    {
        var period = GetUtcPeriod(start, finish);
        var purchases = GetClassificationCountAsync(BudgetEvents.Purchase, period.StartUtc, period.EndUtc, token);
        var incomes = GetClassificationCountAsync(BudgetEvents.Income, period.StartUtc, period.EndUtc, token);
        try
        {
            await Task.WhenAll(purchases, incomes).ConfigureAwait(false);
            return (await purchases.ConfigureAwait(false), await incomes.ConfigureAwait(false));
        }
        catch (HttpRequestException error) when (MissingRoute(error) && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    public (DateTime StartUtc, DateTime EndUtc) GetUtcPeriod(DateTime start, DateTime finish)
    {
        var last = PeriodBoundary(finish, -1);
        return (PeriodBoundary(start, 1), last == DateTime.MaxValue ? last : last.AddTicks(1));
    }

    private static string ClassificationPath(string kind) => kind switch
    {
        BudgetEvents.Purchase => "api/purchases/classification",
        BudgetEvents.Income => "api/incomes/classification",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string ExclusiveUtcRange(DateTime first, DateTime last) =>
        $"startUtc={Format(first)}&endExclusiveUtc={Format(last)}";

    private static string Format(DateTime value) =>
        Uri.EscapeDataString(value.ToString("O", CultureInfo.InvariantCulture));

    internal Task<bool> ApplyPurchaseClassificationAsync(Guid purchaseId, long revision,
        IReadOnlyList<PurchaseClassificationResultDto> items, CancellationToken token) =>
        SendMutationAsync(HttpMethod.Post, FormattableString.Invariant($"api/purchases/{purchaseId:D}/classification?revision={revision}"),
            token, new PurchaseClassificationWriteDto(items), modifiesAmounts: false);

    internal Task<bool> ApplyPurchaseReclassificationAsync(Guid purchaseId, long revision,
        IReadOnlyList<PurchaseClassificationResultDto> items, CancellationToken token) =>
        SendMutationAsync(HttpMethod.Post, FormattableString.Invariant($"api/purchases/{purchaseId:D}/reclassification?revision={revision}"),
            token, new PurchaseClassificationWriteDto(items), modifiesAmounts: false);

    internal Task<bool> ApplyIncomeClassificationAsync(Guid incomeId, long revision, Guid? categoryId,
        CancellationToken token) => SendMutationAsync(HttpMethod.Post,
            FormattableString.Invariant($"api/incomes/{incomeId:D}/classification?revision={revision}"),
            token, new CategorySelectionDto(categoryId), modifiesAmounts: false);

    public Task<bool> DeletePurchaseAsync(Guid id, long revision, CancellationToken cancellationToken = default) =>
        DeleteAsync(FormattableString.Invariant($"api/purchases/{id:D}?revision={revision}"), cancellationToken);

    public Task<bool> DeletePurchaseItemAsync(Guid purchaseId, Guid itemId, long revision,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(FormattableString.Invariant($"api/purchases/{purchaseId:D}/items/{itemId:D}?revision={revision}"), cancellationToken);

    public async Task<PurchaseReceiptSourceDto?> GetPurchaseReceiptSourceAsync(Guid purchaseId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendAsync<PurchaseReceiptSourceDto>(HttpMethod.Get, $"api/purchases/{purchaseId:D}/receipt-source",
                cancellationToken: cancellationToken);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<ServerSettingsDto> GetServerSettingsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<ServerSettingsDto>(HttpMethod.Get, "api/settings", cancellationToken: cancellationToken);

    internal Task<CurrencyRateDto[]> GetExchangeRatesAsync(string target, CurrencyRateKeyDto[] rates,
        CancellationToken cancellationToken) =>
        SendAsync<CurrencyRateDto[]>(HttpMethod.Post, "api/exchange-rates",
            new CurrencyRateQueryDto(target, rates), cancellationToken);

    public Task<ServerSettingsDto> SaveServerSettingsAsync(ServerSettingsWriteDto settings, CancellationToken cancellationToken = default) =>
        SendAsync<ServerSettingsDto>(HttpMethod.Put, "api/settings", settings, cancellationToken, readProblemDetails: true);

    private string DateQuery(DateTime start, DateTime finish)
    {
        var first = PeriodBoundary(start, 1);
        var last = PeriodBoundary(finish, -1);
        return $"start={Format(BudgetTime.InZone(first, BudgetTime.Belgrade))}&finish={Format(BudgetTime.InZone(last, BudgetTime.Belgrade))}" +
            $"&startUtc={Format(first)}&finishUtc={Format(last)}";
    }

    private DateTime PeriodBoundary(DateTime local, int direction)
    {
        if (local == DateTime.MinValue || local == DateTime.MaxValue)
            return BudgetTime.AsUtc(local);
        var zone = settings.DisplayTimeZone;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
            local = local.AddMinutes(direction);
        if (direction < 0 && zone.IsAmbiguousTime(local))
            return new DateTimeOffset(local, zone.GetAmbiguousTimeOffsets(local).Min()).UtcDateTime;
        return BudgetTime.FromLocal(local, zone);
    }

    public async Task<IReadOnlyList<BudgetCategoryDto>> GetBudgetCategoriesAsync(CancellationToken token = default) =>
        await SendAsync<BudgetCategoryDto[]>(HttpMethod.Get, "api/categories", cancellationToken: token).ConfigureAwait(false);

    public Task<BudgetCategoryDto> AddBudgetCategoryAsync(string kind, string name, CancellationToken token = default) =>
        AddBudgetCategoryAsync(new BudgetCategoryWriteDto(kind, name), token);

    public Task<BudgetCategoryDto> AddBudgetCategoryAsync(BudgetCategoryWriteDto category, CancellationToken token = default) =>
        SendAsync<BudgetCategoryDto>(HttpMethod.Post, "api/categories", category, token, readProblemDetails: true);

    public Task<BudgetCategoryDto> UpdateCategoryAsync(Guid id, BudgetCategoryWriteDto category,
        CancellationToken token = default) => SendAsync<BudgetCategoryDto>(HttpMethod.Put, $"api/categories/{id:D}",
            category, token, readProblemDetails: true);

    internal Task<bool> DeleteBudgetCategoryAsync(Guid id, CancellationToken token) =>
        DeleteAsync($"api/categories/{id:D}", token);

    internal async Task<IReadOnlyList<BudgetCategoryDto>?> GetOperationCategoriesAsync(string kind, CancellationToken token = default)
    {
        try
        {
            var categories = await GetBudgetCategoriesAsync(token).ConfigureAwait(false);
            return categories.Where(category => category.Kind == kind).ToArray();
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsConnectionError(error))
        {
            // The optional catalog must not block a financial edit or clear its category.
            return null;
        }
    }

    public async Task<Income> AddIncomeAsync(Income income, CategorySelectionDto? category = null) =>
        ToIncome(await SendAsync<IncomeDto>(HttpMethod.Post, "api/incomes", IncomeWrite(income, category: category)), income.User);

    public async Task<Income> UpdateIncomeAsync(Income income, DateTime? unchangedInstant = null, CategorySelectionDto? category = null) =>
        ToIncome(await SendAsync<IncomeDto>(HttpMethod.Put, $"api/incomes/{income.Id:D}", IncomeWrite(income, unchangedInstant, category)), income.User);

    public Task<bool> DeleteIncomeAsync(Guid id) => DeleteAsync($"api/incomes/{id:D}");

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? payload = null,
        CancellationToken cancellationToken = default, bool readProblemDetails = false,
        Action<HttpResponseMessage>? observeResponse = null) where T : class
    {
        var connection = (settings.ServerUrl, settings.ProtectedApiKey);
        using var request = CreateRequest(method, ParseServerUrl(connection.ServerUrl), settings.ApiKey, path);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        using var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, readProblemDetails, cancellationToken).ConfigureAwait(false);
        if (connection == (settings.ServerUrl, settings.ProtectedApiKey))
            await MarkFinancialMutationAsync(method, path, response).ConfigureAwait(false);
        observeResponse?.Invoke(response);
        return await ReadResponseAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpRequestMessage request, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            var response = await TransportFor(request).SendAsync(request, token).ConfigureAwait(false);
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(2) || !response.IsSuccessStatusCode)
                logger?.Log<CastarServerClient>($"HTTP {request.Method} {request.RequestUri!.AbsolutePath}: {(int)response.StatusCode}, {elapsed.ElapsedMilliseconds} мс.", TypeLog.Info);
            return response;
        }
        catch (Exception error) when (IsConnectionError(error))
        {
            // Never log query strings, authorization headers, response content or financial data.
            if (!token.IsCancellationRequested)
                logger?.Log<CastarServerClient>($"HTTP {request.Method} {request.RequestUri!.AbsolutePath}: {error.GetBaseException().GetType().Name}, {elapsed.ElapsedMilliseconds} мс.", TypeLog.Warning);
            throw;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, bool readProblemDetails,
        CancellationToken cancellationToken)
    {
        if (readProblemDetails && (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict) &&
            response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            ServerSettingsProblem? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync<ServerSettingsProblem>(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Use the HTTP status if the server returned an unreadable problem.
            }
            if (!string.IsNullOrWhiteSpace(problem?.Detail))
                throw new HttpRequestException(problem.Detail, null, response.StatusCode);
        }
        EnsureSuccess(response);
    }

    private sealed record ServerSettingsProblem(string? Detail);

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                ?? throw new HttpRequestException("Сервер вернул пустой ответ.", null, response.StatusCode);
        }
        catch (JsonException error)
        {
            throw new HttpRequestException("Ответ сервера имеет неверный формат.", error, response.StatusCode);
        }
    }

    private Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        SendMutationAsync(HttpMethod.Delete, path, cancellationToken);

    private async Task<bool> SendMutationAsync(HttpMethod method, string path, CancellationToken cancellationToken,
        object? payload = null, bool modifiesAmounts = true)
    {
        var connection = (settings.ServerUrl, settings.ProtectedApiKey);
        using var request = CreateRequest(method, ParseServerUrl(connection.ServerUrl), settings.ApiKey, path);
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        using var response = await SendRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, readProblemDetails: payload is not null, cancellationToken).ConfigureAwait(false);
        if (modifiesAmounts && connection == (settings.ServerUrl, settings.ProtectedApiKey))
            await MarkFinancialMutationAsync(method, path, response).ConfigureAwait(false);
        return true;
    }

    private async Task MarkFinancialMutationAsync(HttpMethod method, string path, HttpResponseMessage response)
    {
        if (method != HttpMethod.Post && method != HttpMethod.Put && method != HttpMethod.Delete) return;
        var route = path.Split('?', 2)[0];
        if (route is "api/purchases/classification/queue" or "api/incomes/classification/queue")
            return;
        if (route is "api/incomes" or "api/purchases"
            || route.StartsWith("api/incomes/", StringComparison.Ordinal)
            || route.StartsWith("api/purchases/", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref statisticsFinancialRevision);
            if (FinancialMutationAsync is { } changed)
            {
                var affected = response.Headers.TryGetValues(BudgetEvents.AffectedFromHeader, out var values)
                    && DateTimeOffset.TryParseExact(values.FirstOrDefault(), "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date) ? date : (DateTimeOffset?)null;
                await changed(affected).ConfigureAwait(false);
            }
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri server, string apiKey, string path)
    {
        if (apiKey.Length < 32 || apiKey.Any(char.IsWhiteSpace))
            throw new ArgumentException(ConnectionNotEstablished);
        var request = new HttpRequestMessage(method, new Uri(server, path))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", apiKey) }
        };
        if (path.StartsWith("api/purchases", StringComparison.Ordinal) || path.StartsWith("api/recurring-purchases", StringComparison.Ordinal))
            request.Headers.Add("X-Castar-Item-Currency", "1");
        return request;
    }

    private HttpClient TransportFor(HttpRequestMessage request) =>
        request.RequestUri!.Scheme == Uri.UriSchemeHttp ? localClient : httpsClient;

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Сервер отклонил ключ API.",
            HttpStatusCode.Forbidden => "Сервер отказал в доступе.",
            HttpStatusCode.BadRequest => "Сервер отклонил данные. Проверьте заполненные поля.",
            HttpStatusCode.NotFound => "Запись или путь API не найдены.",
            HttpStatusCode.Conflict => "Изменение конфликтует с данными на сервере.",
            HttpStatusCode.ServiceUnavailable => "Сервер временно недоступен.",
            _ when (int)response.StatusCode is >= 300 and < 400 => "Сервер перенаправил запрос. Укажите конечный адрес.",
            _ => "Сервер вернул ошибку."
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static UserWriteDto UserWrite(User user) =>
        new(user.TelegramId, user.Name, user.Sex, user.ColorRaw, user.ImageRaw);

    private IncomeWriteDto IncomeWrite(Income income, DateTime? unchangedInstant = null, CategorySelectionDto? category = null)
    {
        var instant = unchangedInstant ?? ReadSelectedTime(income.AddedOn);
        return new(income.User?.Id ?? income.UserId, income.Sum, income.Source, BudgetTime.InZone(instant, BudgetTime.Belgrade))
        {
            Currency = income.Currency, AddedOnUtc = BudgetTime.AsUtc(instant), Category = category
        };
    }

    private PurchaseWriteDto WritePurchaseTime(PurchaseWriteDto draft)
    {
        var instant = draft.AddedOnUtc?.UtcDateTime ?? ReadSelectedTime(draft.AddedOn);
        return draft with { AddedOn = BudgetTime.InZone(instant, BudgetTime.Belgrade), AddedOnUtc = instant };
    }

    private DateTime ReadSelectedTime(DateTime local)
    {
        if (settings.DisplayTimeZone.IsInvalidTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)))
            throw new HttpRequestException("Это время пропущено при переводе часов. Укажите другое время.");
        return BudgetTime.FromLocal(local, settings.DisplayTimeZone);
    }

    internal DateTime? DisplayOperationDate(DateTime? belgrade, DateTimeOffset? utc) =>
        utc is { } instant ? BudgetTime.InZone(instant.UtcDateTime, settings.DisplayTimeZone)
            : belgrade is { } date ? BudgetTime.InZone(ReadLegacyDate(date), settings.DisplayTimeZone) : null;

    private static DateTime ReadLegacyDate(DateTime date) => date.Kind == DateTimeKind.Utc
        ? date : BudgetTime.FromLocal(date, BudgetTime.Belgrade);

    private static User ToUser(UserDto dto) => new()
    {
        Id = dto.Id, TelegramId = dto.TelegramId, Name = dto.Name, Sex = dto.Sex,
        ColorRaw = dto.ColorRaw, ImageRaw = dto.ImageRaw,
        TelegramNotificationsEnabled = dto.TelegramNotificationsEnabled ?? true
    };

    private static Income ToIncome(IncomeDto dto, User? user) => new()
    {
        Id = dto.Id, UserId = dto.UserId, User = user ?? ToUser(dto.User), MessageId = dto.MessageId,
        Sum = dto.Sum, Currency = dto.Currency, Source = dto.Source,
        AddedOn = dto.AddedOnUtc?.UtcDateTime ?? ReadLegacyDate(dto.AddedOn),
        RateDate = dto.RateDate ?? DateOnly.FromDateTime(dto.AddedOn), CategoryId = dto.CategoryId,
        CategoryState = dto.CategoryConfirmed ? IncomeCategoryState.Confirmed : IncomeCategoryState.Pending
    };

    public void Dispose()
    {
        localClient.Dispose();
        httpsClient.Dispose();
    }

    private sealed record ServerStatus(int ApiVersion);
}
