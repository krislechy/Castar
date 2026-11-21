using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Castar.Api;
using Castar.BL;
using Castar.DAL;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using Castar.Domain.Models.SettingsModel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static partial class CastarEndpoints
{
    private static readonly SemaphoreSlim UserWrites = new(1);
    private static readonly JsonSerializerOptions SummaryJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public static RouteGroupBuilder MapCastarApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        api.AddEndpointFilter(HandleDatabaseConflicts);
        api.MapExchangeRates();
        api.MapPost("/backup", DatabaseBackup.DownloadAsync);
        api.MapGet("/settings", (ServerSettings settings) => Results.Ok(settings.Read()));
        api.MapPut("/settings", UpdateServerSettingsAsync);
        api.MapGet("/users", GetUsers);
        api.MapGet("/users/{id:guid}", GetUser);
        api.MapGet("/users/{id:guid}/avatar", GetUserAvatar);
        api.MapPost("/users", CreateUser).AddEndpointFilter(SerializeUserWrites);
        api.MapPut("/users/{id:guid}", UpdateUser).AddEndpointFilter(SerializeUserWrites);
        api.MapDelete("/users/{id:guid}", DeleteUser);

        api.MapGet("/categories", GetCategories);
        api.MapPost("/categories", CreateCategory);
        api.MapPut("/categories/{id:guid}", UpdateCategory);

        api.MapGet("/incomes", GetIncomes);
        api.MapGet("/incomes/{id:guid}", GetIncome);
        api.MapPost("/incomes", CreateIncome);
        api.MapPut("/incomes/{id:guid}", UpdateIncome);
        api.MapDelete("/incomes/{id:guid}", DeleteIncome);
        api.MapGet("/incomes/classification/count", CountUncategorizedIncomes);
        api.MapGet("/incomes/classification/pending", GetPendingIncomeClassification);
        api.MapPost("/incomes/classification/queue", QueueUncategorizedIncomes);
        api.MapPost("/incomes/{id:guid}/classification", ApplyIncomeClassification);

        api.MapGet("/purchases", GetPurchases);
        api.MapGet("/purchases/summary", GetPurchaseSummaries);
        api.MapGet("/purchases/classification/pending", GetPendingPurchaseClassification);
        api.MapGet("/purchases/classification/count", CountUncategorizedPurchases);
        api.MapPost("/purchases/classification/queue", QueueUncategorizedPurchaseItems);
        api.MapPost("/purchases/{purchaseId:guid}/classification", ApplyPurchaseClassification);
        api.MapGet("/purchases/{purchaseId:guid}", GetPurchaseDetails);
        api.MapGet("/budget/operations", GetBudgetOperations);
        api.MapPost("/purchases", CreatePurchase);
        api.MapGet("/purchases/{purchaseId:guid}/receipt-source", GetPurchaseReceiptSource);
        api.MapPut("/purchases/{purchaseId:guid}", EditPurchase);
        api.MapPut("/purchases/{purchaseId:guid}/items/{itemId:guid}", EditPurchaseItem);
        api.MapPost("/purchases/{purchaseId:guid}/items", AddPurchaseItem);
        api.MapDelete("/purchases/{purchaseId:guid}", DeletePurchase);
        api.MapDelete("/purchases/{purchaseId:guid}/items/{itemId:guid}", DeletePurchaseItem);
        return api;
    }

    private static Task<IResult> UpdateServerSettingsAsync(ServerSettings settings, ServerSettingsWriteDto requested,
        HttpContext context, CancellationToken token)
    {
        if ((requested.TelegramToken is not null || requested.NalogCredentials is not null ||
            requested.ReceiptAuthentication?.Values is not null) && !context.Request.IsHttps)
        {
            var address = context.Connection.RemoteIpAddress;
            if (address?.IsIPv4MappedToIPv6 == true)
                address = address.MapToIPv4();
            if (address is null || !IPAddress.IsLoopback(address))
                return Task.FromResult(Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    detail: "Для передачи токена или пароля подключитесь по HTTPS."));
        }

        return settings.UpdateAsync(requested, token);
    }

    private static async ValueTask<object?> HandleDatabaseConflicts(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            return await next(context);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Запись уже изменилась", detail: "Обновите данные и повторите действие.");
        }
        catch (OverflowException) when (context.HttpContext.Request.Path.StartsWithSegments("/api/purchases"))
        {
            return InvalidInput("Итог покупки выходит за допустимый диапазон.");
        }
        catch (Exception error) when (IsForeignKeyConflict(error) || IsForeignKeyConflict(error.InnerException))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Связанные записи", detail: "Проверьте пользователя и связанные с ним покупки или начисления.");
        }
    }

    private static bool IsForeignKeyConflict(Exception? error) =>
        error is SqliteException { SqliteExtendedErrorCode: 787 };

    private static async ValueTask<object?> SerializeUserWrites(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        await UserWrites.WaitAsync(context.HttpContext.RequestAborted);
        try
        {
            return await next(context);
        }
        finally
        {
            UserWrites.Release();
        }
    }

    private static async Task<UserDto[]> GetUsers(IUsersService users) =>
        (await users.GetEntities()).Select(ToDto).ToArray();

    private static async Task<IResult> GetUser(Guid id, IUsersRepository users, CancellationToken cancellationToken)
    {
        var user = await users.Find(id, cancellationToken);
        return user is null ? Results.NotFound() : Results.Ok(ToDto(user));
    }

    private static async Task<IResult> GetUserAvatar(Guid id, CastarDbContext database,
        HttpContext context, CancellationToken cancellationToken)
    {
        var image = await database.Users.AsNoTracking().Where(user => user.Id == id)
            .Select(user => user.ImageRaw).SingleOrDefaultAsync(cancellationToken);
        if (image is not { Length: > 0 })
            return Results.NotFound();

        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.Stream(new MemoryStream(image, writable: false), AvatarContentType(image));
    }

    private static string AvatarContentType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]))
            return "image/png";
        if (bytes.StartsWith((ReadOnlySpan<byte>)[255, 216, 255]))
            return "image/jpeg";
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
            return "image/gif";
        if (bytes.StartsWith("BM"u8))
            return "image/bmp";
        return "application/octet-stream";
    }

    private static async Task<IResult> CreateUser(UserWriteDto input, IUsersService users, BudgetEventPublisher events,
        CancellationToken cancellationToken)
    {
        if (Validate(input) is { } invalid)
            return invalid;
        if (await IsTelegramIdAssigned(input.TelegramId, users, cancellationToken))
            return DuplicateTelegramId();

        var user = new User();
        Apply(user, input);
        await users.Add(user);
        events.QueueSynchronization(ServerSynchronization.Users);
        return Results.Created($"/api/users/{user.Id}", ToDto(user));
    }

    private static async Task<IResult> UpdateUser(Guid id, UserWriteDto input, IUsersRepository repository,
        IUsersService users, BudgetEventPublisher events, CancellationToken cancellationToken)
    {
        if (Validate(input) is { } invalid)
            return invalid;

        var user = await repository.Find(id, cancellationToken);
        if (user is null)
            return Results.NotFound();
        if (user.TelegramId != input.TelegramId && await IsTelegramIdAssigned(input.TelegramId, users, cancellationToken))
            return DuplicateTelegramId();

        Apply(user, input);
        await users.Update(user);
        events.QueueSynchronization(ServerSynchronization.Users);
        return Results.Ok(ToDto(user));
    }

    private static async Task<IResult> DeleteUser(Guid id, IUsersService users, BudgetEventPublisher events)
    {
        if (!await users.Delete(id))
            return Results.NotFound();
        events.QueueSynchronization(ServerSynchronization.Users);
        return Results.NoContent();
    }

    private static async Task<IResult> GetIncomes(DateTime start, DateTime finish,
        DateTimeOffset? startUtc, DateTimeOffset? finishUtc, IIncomesService incomes)
    {
        if (!TryReadRange(start, finish, startUtc, finishUtc, out var first, out var last))
            return InvalidInput("Укажите допустимый период.");

        var entries = await incomes.GetEntitiesWithAllIncludes(first, last);
        return Results.Ok(entries.Select(ToDto).ToArray());
    }

    private static async Task<IResult> GetIncome(Guid id, IIncomesRepository incomes,
        IUsersRepository users, CancellationToken cancellationToken)
    {
        var income = await incomes.Find(id, cancellationToken);
        if (income is null)
            return Results.NotFound();

        var owner = await users.Find(income.UserId, cancellationToken);
        if (owner is null)
            return Results.NotFound();

        income.User = owner;
        return Results.Ok(ToDto(income));
    }

    private static async Task<IResult> CreateIncome(IncomeWriteDto input, IIncomesService incomes,
        IUsersRepository users, SettingsModel settings, CastarDbContext db, CancellationToken cancellationToken)
    {
        var policy = settings.ManualCurrencies;
        var currency = input.Currency is null ? policy.DefaultCurrency : ManualCurrencySettings.NormalizeCode(input.Currency);
        if (!policy.Allows(currency))
            return InvalidManualCurrency(policy);
        if (Validate(input, usesLegacyPrecision: false) is { } invalid)
            return invalid;
        if (!TryReadTime(input.AddedOn, input.AddedOnUtc, out var date))
            return InvalidInput("Укажите допустимую дату начисления.");

        var owner = await users.Find(input.UserId, cancellationToken);
        if (owner is null)
            return InvalidInput("Выберите существующего пользователя.");
        if (!await IsValidCategoryAsync(input.Category, BudgetEvents.Income, db, cancellationToken))
            return InvalidInput("Выберите существующую категорию доходов.");

        var income = new Income
        {
            CategoryId = input.Category?.Id,
            CategoryState = input.Category is null ? IncomeCategoryState.Pending : IncomeCategoryState.Confirmed
        };
        Apply(income, input with { Currency = currency }, owner, date);
        await incomes.Add(income, owner.TelegramId, owner);
        return Results.Created($"/api/incomes/{income.Id}", ToDto(income));
    }

    private static async Task<IResult> UpdateIncome(Guid id, IncomeWriteDto input, IIncomesRepository repository,
        IIncomesService incomes, IUsersRepository users, SettingsModel settings, CastarDbContext db,
        CancellationToken cancellationToken)
    {
        var income = await repository.Find(id, cancellationToken);
        if (income is null)
            return Results.NotFound();

        if (input.Currency is not null)
        {
            var policy = settings.ManualCurrencies;
            if (ManualCurrencySettings.NormalizeCode(input.Currency) is not { } currency ||
                (!string.Equals(currency, income.Currency, StringComparison.OrdinalIgnoreCase) && !policy.Allows(currency)))
                return InvalidManualCurrency(policy);
            input = input with { Currency = currency };
        }
        if (Validate(input, usesLegacyPrecision: (input.Currency ?? income.Currency) is null) is { } invalid)
            return invalid;
        if (!TryReadTime(input.AddedOn, input.AddedOnUtc, out var date, income.AddedOn))
            return InvalidInput("Укажите допустимую дату начисления.");

        var owner = await users.Find(input.UserId, cancellationToken);
        if (owner is null)
            return InvalidInput("Выберите существующего пользователя.");
        if (!await IsValidCategoryAsync(input.Category, BudgetEvents.Income, db, cancellationToken))
            return InvalidInput("Выберите существующую категорию доходов.");

        Apply(income, input, owner, date);
        if (input.Category is not null)
            income.CategoryId = input.Category.Id;
        await incomes.Update(income, updateCategory: input.Category is not null);
        return Results.Ok(ToDto(income));
    }

    private static async Task<IResult> DeleteIncome(Guid id, IIncomesService incomes) =>
        await incomes.Delete(id) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> GetPurchases(DateTime start, DateTime finish,
        DateTimeOffset? startUtc, DateTimeOffset? finishUtc, IPurchasesService purchases, CastarDbContext db, CancellationToken token)
    {
        if (!TryReadRange(start, finish, startUtc, finishUtc, out var first, out var last))
            return InvalidInput("Укажите допустимый период.");

        var entries = await purchases.GetEntitiesWithAllIncludes(first, last);
        var categories = await ReadItemCategories(db, entries.SelectMany(entry => entry.Items), token);
        return Results.Ok(entries.Select(purchase => PurchaseDtoFrom(purchase,
            new PurchaseOwnerDto(purchase.User.Name, purchase.User.ImageRaw is { Length: > 0 }),
            purchase.Items, deferItems: false, categories)).ToArray());
    }

    private static async Task<IResult> GetBudgetOperations(DateTime start, DateTime finish,
        DateTimeOffset? startUtc, DateTimeOffset? finishUtc, CastarDbContext db, HttpResponse response,
        CancellationToken token, bool includeOpening = false)
    {
        if (!TryReadRange(start, finish, startUtc, finishUtc, out var first, out var last))
            return InvalidInput("Укажите допустимый период.");

        await db.Database.OpenConnectionAsync(token);
        await using var snapshot = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var transaction = await db.Database.UseTransactionAsync(snapshot, token);
        var incomes = await db.Incomes.AsNoTracking().Where(row => row.AddedOn >= first && row.AddedOn <= last)
            .Select(row => new { row.Currency, row.Sum, row.AddedOn, row.RateDate }).ToListAsync(token);
        var purchases = await db.Purchases.AsNoTracking().Where(row => row.AddedOn >= first && row.AddedOn <= last)
            .Select(row => new { row.Currency, row.TotalSum, row.AddedOn, row.RateDate, row.ReceiptDate }).ToListAsync(token);
        var operations = incomes.Select(row => new BudgetOperationDto(true, row.Currency, row.Sum,
                BudgetTime.AsUtc(row.AddedOn), row.RateDate ?? DateOnly.FromDateTime(BudgetTime.InZone(row.AddedOn, BudgetTime.Belgrade))))
            .Concat(purchases.Select(row => new BudgetOperationDto(false, row.Currency, row.TotalSum,
                row.AddedOn is { } date ? BudgetTime.AsUtc(date) : null,
                row.RateDate ?? row.ReceiptDate ?? (row.AddedOn is { } instant
                    ? DateOnly.FromDateTime(BudgetTime.InZone(instant, BudgetTime.Belgrade)) : null),
                row.ReceiptDate)));
        if (includeOpening)
        {
            operations = operations.Concat(await GetBudgetOpeningOperations(db, first, token));
            response.Headers["X-Castar-Budget-Opening"] = "1";
        }
        return Results.Ok(operations.ToArray());
    }

    private static async Task<IEnumerable<BudgetOperationDto>> GetBudgetOpeningOperations(
        CastarDbContext db, DateTime first, CancellationToken token)
    {
        // Keep equal native amounts together so historical FX still rounds each operation before counting it.
        var incomes = await db.Incomes.AsNoTracking().Where(row => row.AddedOn < first && row.RateDate != null)
            .GroupBy(row => new { row.Currency, row.RateDate, row.Sum })
            .Select(group => new BudgetOperationDto(true, group.Key.Currency, group.Key.Sum, null, group.Key.RateDate)
            {
                IsOpening = true,
                OpeningCount = group.LongCount()
            }).ToListAsync(token);
        var purchases = await db.Purchases.AsNoTracking()
            .Where(row => row.AddedOn < first && (row.RateDate != null || row.ReceiptDate != null))
            .GroupBy(row => new { row.Currency, Date = row.RateDate ?? row.ReceiptDate, row.TotalSum })
            .Select(group => new BudgetOperationDto(false, group.Key.Currency, group.Key.TotalSum, null, group.Key.Date)
            {
                IsOpening = true,
                OpeningCount = group.LongCount()
            }).ToListAsync(token);

        // Legacy rows need the Belgrade calendar day, including its historical UTC offset.
        var legacyIncomes = await db.Incomes.AsNoTracking().Where(row => row.AddedOn < first && row.RateDate == null)
            .Select(row => new { row.Currency, row.Sum, row.AddedOn }).ToListAsync(token);
        var legacyPurchases = await db.Purchases.AsNoTracking()
            .Where(row => row.AddedOn < first && row.RateDate == null && row.ReceiptDate == null)
            .Select(row => new { row.Currency, row.TotalSum, row.AddedOn }).ToListAsync(token);
        var legacy = legacyIncomes.Select(row => new BudgetOperationDto(true, row.Currency, row.Sum, null,
                BudgetTime.BelgradeDate(row.AddedOn)))
            .Concat(legacyPurchases.Select(row => new BudgetOperationDto(false, row.Currency, row.TotalSum, null,
                BudgetTime.BelgradeDate(row.AddedOn!.Value))))
            .GroupBy(row => new { row.IsIncome, row.Currency, row.RateDate, row.Amount })
            .Select(group => group.First() with { IsOpening = true, OpeningCount = group.LongCount() });
        return incomes.Concat(purchases).Concat(legacy);
    }

    private static async Task<IResult> GetPurchaseSummaries(DateTime start, DateTime finish,
        DateTimeOffset? startUtc, DateTimeOffset? finishUtc, CastarDbContext db, CancellationToken token)
    {
        if (!TryReadRange(start, finish, startUtc, finishUtc, out var first, out var last))
            return InvalidInput("Укажите допустимый период.");

        var rows = await db.Purchases.AsNoTracking().Where(row => row.AddedOn >= first && row.AddedOn <= last)
            .ToListAsync(token);
        if (rows.Count == 0)
            return Results.Ok(Array.Empty<PurchaseDto>());

        var ownerIds = rows.Select(row => row.UserId).Distinct().ToArray();
        var owners = await db.Users.AsNoTracking().Where(user => ownerIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                Owner = new PurchaseOwnerDto(user.Name, user.ImageRaw != null && user.ImageRaw.Length > 0)
            }).ToDictionaryAsync(user => user.Id, user => user.Owner, token);

        var editedIds = rows.Where(row => row.Revision != 0 && row.OriginalReceiptSnapshot != null)
            .Select(row => row.Id).ToArray();
        var changedLines = editedIds.Length == 0 ? [] : await db.Items.AsNoTracking()
            .Where(item => editedIds.Contains(item.PurchaseId)).ToListAsync(token);
        var lines = changedLines.ToLookup(item => item.PurchaseId);
        var result = rows.Select(row => PurchaseDtoFrom(row, owners.GetValueOrDefault(row.UserId),
            lines[row.Id], deferItems: true)).ToArray();
        return Results.Json(result, SummaryJson);
    }

    private static async Task<IResult> GetPurchaseDetails(Guid purchaseId, IPurchasesRepository purchases,
        CastarDbContext db, CancellationToken token)
    {
        var purchase = await purchases.FindWithItemsAsync(purchaseId, token);
        if (purchase is null)
            return Results.NotFound();
        var owner = await db.Users.AsNoTracking().Where(user => user.Id == purchase.UserId)
            .Select(user => new PurchaseOwnerDto(user.Name, user.ImageRaw != null && user.ImageRaw.Length > 0))
            .SingleAsync(token);
        var categories = await ReadItemCategories(db, purchase.Items, token);
        return Results.Ok(PurchaseDtoFrom(purchase, owner, purchase.Items, deferItems: false, categories));
    }

    private static async Task<IResult> GetPurchaseReceiptSource(Guid purchaseId, IPurchasesRepository purchases,
        CancellationToken token)
    {
        var (found, source, isLegacy) = await purchases.GetReceiptSourceAsync(purchaseId, token);
        return found ? Results.Ok(new PurchaseReceiptSourceDto(source, isLegacy)) : Results.NotFound();
    }

    private static async Task<IResult> DeletePurchase(Guid purchaseId, long revision, IPurchasesRepository purchases,
        CancellationToken token) => revision < 0
        ? InvalidInput("Укажите версию покупки.")
        : PurchaseMutation(await purchases.DeletePurchaseAsync(purchaseId, revision, token));

    private static async Task<IResult> DeletePurchaseItem(Guid purchaseId, Guid itemId, long revision,
        IPurchasesRepository purchases, CancellationToken token) => revision < 0
        ? InvalidInput("Укажите версию покупки.")
        : PurchaseMutation(await purchases.DeletePurchaseItemAsync(purchaseId, itemId, revision, token));

    private static async Task<IResult> EditPurchase(Guid purchaseId, long revision, PurchaseWriteDto input,
        IPurchasesRepository purchases, SettingsModel settings, CastarDbContext db, CancellationToken token)
    {
        if (revision < 0)
            return InvalidInput("Укажите версию покупки.");
        if (ValidatePurchase(input) is { } invalid)
            return invalid;
        if (!TryReadTime(input.AddedOn, input.AddedOnUtc, out var date))
            return InvalidInput("Укажите дату покупки.");
        if (!await IsValidCategoryAsync(input.Category, BudgetEvents.Purchase, db, token))
            return InvalidInput("Выберите существующую категорию расходов.");

        var policy = settings.ManualCurrencies;
        var result = await purchases.EditPurchaseAsync(purchaseId, revision, input.Name, input.RetailPlace,
            input.RetailPlaceAddress, date, input.Currency, policy, token, input.UserId,
            legacyDate: input.AddedOnUtc is null ? input.AddedOn : null,
            categoryId: input.Category?.Id, updateCategory: input.Category is not null);
        if (result == PurchaseMutationResult.InvalidOwner)
            return InvalidInput("Выберите существующего пользователя.");
        return result == PurchaseMutationResult.InvalidCurrency ? InvalidManualCurrency(policy) : PurchaseMutation(result);
    }

    private static async Task<IResult> EditPurchaseItem(Guid purchaseId, Guid itemId, long revision,
        PurchaseItemWriteDto input, IPurchasesRepository purchases, CastarDbContext db,
        BudgetEventPublisher events, CancellationToken token)
    {
        if (revision < 0)
            return InvalidInput("Укажите версию покупки.");
        if (ValidatePurchaseItem(input) is { } invalid)
            return invalid;
        if (!await IsValidCategoryAsync(input.Category, BudgetEvents.Purchase, db, token))
            return InvalidInput("Выберите существующую категорию расходов.");

        var result = await purchases.EditPurchaseItemAsync(purchaseId, itemId, revision, input.Name,
            input.Quantity, input.Price, token, input.Category?.Id, input.Category is not null);
        return CompleteManualCategoryWrite(result, input.Category, events);
    }

    private static async Task<IResult> CreatePurchase(PurchaseCreateDto input, IPurchasesRepository purchases,
        IUsersRepository users, SettingsModel settings, CastarDbContext db, BudgetEventPublisher events, CancellationToken token)
    {
        if (input.Id == Guid.Empty || input.Purchase is null || input.Items is not { Length: > 0 and <= 250 })
            return InvalidInput("Добавьте от 1 до 250 товаров.");
        var header = input.Purchase;
        if (ValidatePurchase(header) is { } invalid)
            return invalid;
        if (!TryReadTime(header.AddedOn, header.AddedOnUtc, out var date))
            return InvalidInput("Укажите дату покупки.");
        var policy = settings.ManualCurrencies;
        var currency = header.Currency is null ? policy.DefaultCurrency : ManualCurrencySettings.NormalizeCode(header.Currency);
        if (!policy.Allows(currency))
            return InvalidManualCurrency(policy);
        if (header.UserId is not { } ownerId || await users.Find(ownerId, token) is null)
            return InvalidInput("Выберите существующего пользователя.");
        if (!await IsValidCategoryAsync(header.Category, BudgetEvents.Purchase, db, token))
            return InvalidInput("Выберите существующую категорию расходов.");

        var purchase = new Purchase
        {
            Id = input.Id, UserId = ownerId, CategoryId = header.Category?.Id, AddedOn = date, Currency = currency, Name = header.Name,
            RetailPlace = header.RetailPlace, RetailPlaceAddress = header.RetailPlaceAddress
        };
        foreach (var item in input.Items)
        {
            if (item is null)
                return InvalidInput("Заполните данные товара.");
            if (ValidatePurchaseItem(item) is { } invalidItem)
                return invalidItem;
            purchase.Items.Add(new Item
            {
                Name = item.Name, Quantity = item.Quantity, Price = item.Price,
                BudgetCategoryId = item.Category?.Id,
                CategoryState = item.Category is null ? ItemCategoryState.Pending : ItemCategoryState.Confirmed
            });
        }
        var categoryIds = purchase.Items.Select(item => item.BudgetCategoryId).OfType<Guid>().Distinct().ToArray();
        if (categoryIds.Length > 0 && await db.BudgetCategories
            .CountAsync(category => category.Kind == BudgetEvents.Purchase && categoryIds.Contains(category.Id), token) != categoryIds.Length)
            return InvalidInput("Выберите существующие категории расходов.");
        if (await purchases.FindWithItemsAsync(input.Id, token) is { } existing)
            return ConfirmPurchaseCreation(existing, purchase);
        try
        {
            await purchases.Add(purchase, token);
        }
        catch (DbUpdateException error) when (error.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 })
        {
            return ConfirmPurchaseCreation(await purchases.FindWithItemsAsync(input.Id, token), purchase);
        }
        if (purchase.Items.Any(item => item.CategoryState == ItemCategoryState.Confirmed))
            events.Queue(default(BudgetChange).AddCreation(purchase.Id, purchase.AddedOn), default);
        return Results.NoContent();
    }

    private static IResult ConfirmPurchaseCreation(Purchase? existing, Purchase requested)
    {
        if (existing is { MessageId: 0, RawData: null, OriginalReceiptSnapshot: null } &&
            existing.UserId == requested.UserId && existing.AddedOn == requested.AddedOn &&
            existing.Name == requested.Name && existing.Currency == requested.Currency && existing.CategoryId == requested.CategoryId &&
            existing.RetailPlace == requested.RetailPlace && existing.RetailPlaceAddress == requested.RetailPlaceAddress &&
            existing.Items.Count == requested.Items.Count &&
            existing.Items.Zip(requested.Items).All(pair =>
                pair.First.Name == pair.Second.Name && pair.First.Quantity == pair.Second.Quantity &&
                pair.First.Price == pair.Second.Price &&
                (pair.Second.CategoryState != ItemCategoryState.Confirmed ||
                    pair.First.CategoryState == ItemCategoryState.Confirmed &&
                    pair.First.BudgetCategoryId == pair.Second.BudgetCategoryId)))
            return Results.NoContent();

        return PurchaseMutation(PurchaseMutationResult.Conflict);
    }

    private static async Task<IResult> AddPurchaseItem(Guid purchaseId, long revision, PurchaseItemWriteDto input,
        IPurchasesRepository purchases, CastarDbContext db, BudgetEventPublisher events, CancellationToken token)
    {
        if (revision < 0)
            return InvalidInput("Укажите версию покупки.");
        if (ValidatePurchaseItem(input) is { } invalid)
            return invalid;
        if (!await IsValidCategoryAsync(input.Category, BudgetEvents.Purchase, db, token))
            return InvalidInput("Выберите существующую категорию расходов.");

        var result = await purchases.AddPurchaseItemAsync(purchaseId, revision, input.Name,
            input.Quantity, input.Price, token, input.Category?.Id, input.Category is not null);
        return CompleteManualCategoryWrite(result, input.Category, events);
    }

    private static IResult? ValidatePurchase(PurchaseWriteDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 400)
            return InvalidInput("Укажите название покупки длиной до 400 символов.");
        if (input.RetailPlace is { Length: > 400 } || input.RetailPlaceAddress is { Length: > 400 })
            return InvalidInput("Название магазина и адрес могут содержать до 400 символов каждое.");
        return null;
    }

    private static IResult? ValidatePurchaseItem(PurchaseItemWriteDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 400)
            return InvalidInput("Укажите название товара длиной до 400 символов.");
        if (!double.IsFinite(input.Quantity) || Math.Abs(input.Quantity) >= 1_000_000 || (decimal)input.Quantity == 0)
            return InvalidInput("Укажите ненулевое количество меньше 1 000 000 по модулю.");
        const decimal priceLimit = 10000000000000000000000m;
        if (input.Price <= -priceLimit || input.Price >= priceLimit)
            return InvalidInput("Укажите допустимую цену товара.");

        return null;
    }

    private static IResult PurchaseMutation(PurchaseMutationResult result) => result switch
    {
        PurchaseMutationResult.Applied or PurchaseMutationResult.Unchanged => Results.NoContent(),
        PurchaseMutationResult.NotFound => Results.NotFound(),
        PurchaseMutationResult.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict,
            title: "Покупка уже изменилась", detail: "Обновите данные и повторите действие."),
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };

    private static IResult? Validate(UserWriteDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200)
            return InvalidInput("Укажите имя пользователя длиной до 200 символов.");
        if (input.Sex is not (0 or 1) || input.ColorRaw is not { Length: 4 })
            return InvalidInput("Проверьте пол и цвет пользователя.");

        return null;
    }

    private static IResult? Validate(IncomeWriteDto input, bool usesLegacyPrecision)
    {
        const decimal amountLimit = 10000000000000000000000m;
        var amount = usesLegacyPrecision ? decimal.Round(input.Sum, 2, MidpointRounding.AwayFromZero) : input.Sum;
        if (amount <= -amountLimit || amount >= amountLimit || amount == 0)
            return InvalidInput(usesLegacyPrecision
                ? "Укажите ненулевую сумму с точностью до копеек."
                : "Укажите допустимую ненулевую сумму.");
        if (input.AddedOn < new DateTime(1753, 1, 1) || input.AddedOn > new DateTime(9999, 12, 31, 23, 59, 59, 997))
            return InvalidInput("Укажите допустимую дату начисления.");
        if (string.IsNullOrWhiteSpace(input.Source) || input.Source.Length > 400)
            return InvalidInput("Укажите источник начисления длиной до 400 символов.");

        return null;
    }

    private static IResult InvalidManualCurrency(ManualCurrencySettings policy) =>
        InvalidInput($"Доступные валюты: {string.Join(", ", policy.Currencies)}.");

    private static IResult InvalidInput(string message) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Проверьте данные", detail: message);

    private static async Task<bool> IsTelegramIdAssigned(
        long telegramId, IUsersService users, CancellationToken cancellationToken) =>
        telegramId != 0 && await users.GetUserByTelegramId(telegramId, cancellationToken) is not null;

    private static IResult DuplicateTelegramId() =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Telegram ID уже используется",
            detail: "Укажите идентификатор другого аккаунта или оставьте 0 для пользователя без Telegram.");

    private static void Apply(User user, UserWriteDto input)
    {
        user.TelegramId = input.TelegramId;
        user.Name = input.Name;
        user.Sex = input.Sex;
        user.ColorRaw = input.ColorRaw;
        user.ImageRaw = input.ImageRaw;
        if (input.TelegramNotificationsEnabled is { } notificationsEnabled)
            user.TelegramNotificationsEnabled = notificationsEnabled;
    }

    private static void Apply(Income income, IncomeWriteDto input, User owner, DateTime date)
    {
        income.User = owner;
        income.Source = input.Source;
        income.Sum = input.Sum;
        income.Currency = input.Currency?.ToUpperInvariant() ?? income.Currency;
        income.AddedOn = date;
    }

    private static bool TryReadRange(DateTime start, DateTime finish, DateTimeOffset? startUtc, DateTimeOffset? finishUtc,
        out DateTime first, out DateTime last)
    {
        var validStart = TryReadTime(start, startUtc, out first, allowExtremes: true);
        var validFinish = TryReadTime(finish, finishUtc, out last, allowExtremes: true);
        return startUtc.HasValue == finishUtc.HasValue && validStart && validFinish && first <= last;
    }

    private static bool TryReadTime(DateTime legacy, DateTimeOffset? utc, out DateTime time,
        DateTime? current = null, bool allowExtremes = false)
    {
        time = default;
        if (utc is { } instant)
        {
            time = instant.UtcDateTime;
            return allowExtremes || time != default;
        }

        legacy = DateTime.SpecifyKind(legacy, DateTimeKind.Unspecified);
        if ((!allowExtremes && legacy == default) || BudgetTime.Belgrade.IsInvalidTime(legacy))
            return false;
        time = current is { } existing && BudgetTime.InZone(existing, BudgetTime.Belgrade) == legacy
            ? BudgetTime.AsUtc(existing)
            : BudgetTime.FromLocal(legacy, BudgetTime.Belgrade);
        return true;
    }

    private static UserDto ToDto(User user) =>
        new(user.Id, user.TelegramId, user.Name, user.Sex, user.ColorRaw, user.ImageRaw)
        {
            TelegramNotificationsEnabled = user.TelegramNotificationsEnabled,
            HasAvatar = user.ImageRaw is { Length: > 0 }
        };

    private static IncomeDto ToDto(Income income) =>
        new(income.Id, income.UserId, ToDto(income.User) with { ImageRaw = null }, income.MessageId,
            income.Sum, income.Source, BudgetTime.InZone(income.AddedOn, BudgetTime.Belgrade))
        {
            CategoryId = income.CategoryId, CategoryConfirmed = income.CategoryState == IncomeCategoryState.Confirmed,
            Currency = income.Currency,
            AddedOnUtc = BudgetTime.AsUtc(income.AddedOn), RateDate = income.RateDate
        };

    private static PurchaseDto PurchaseDtoFrom(Purchase purchase, PurchaseOwnerDto? owner,
        IEnumerable<Item> lines, bool deferItems, IReadOnlyDictionary<Guid, BudgetCategoryDto>? itemCategories = null)
    {
        var snapshot = deferItems && purchase.Revision == 0 ? null : FiscalReceiptSnapshot.Read(purchase.OriginalReceiptSnapshot);
        var originalLines = snapshot?.Items.ToHashSet();
        bool LineChanged(Item item) => originalLines is not null && !originalLines.Contains(FiscalReceiptLine.Capture(item));
        var items = deferItems ? [] : lines.Select(item =>
        {
            var category = item.BudgetCategoryId is { } categoryId ? itemCategories?.GetValueOrDefault(categoryId) : null;
            return new PurchaseItemDto(item.Id, item.Name, null, item.Quantity, item.Price, item.Sum)
            {
                BudgetCategoryId = item.BudgetCategoryId,
                BudgetCategoryName = category?.Name,
                BudgetCategoryIconKey = BudgetCategoryIconKeys.Canonicalize(BudgetEvents.Purchase, category?.IconKey),
                CategoryConfirmed = item.CategoryState == ItemCategoryState.Confirmed,
                IsEdited = LineChanged(item)
            };
        }).ToArray();
        return new(purchase.Id, purchase.UserId, purchase.Name, purchase.TotalSum, purchase.Currency, purchase.MessageId,
            purchase.AddedOn is { } date ? BudgetTime.InZone(date, BudgetTime.Belgrade) : null,
            purchase.RetailPlace, purchase.RetailPlaceAddress, items)
        {
            CategoryId = purchase.CategoryId,
            ReceiptDate = purchase.ReceiptDate,
            AddedOnUtc = purchase.AddedOn is { } dateUtc ? BudgetTime.AsUtc(dateUtc) : null,
            RateDate = purchase.RateDate,
            Revision = purchase.Revision,
            HasReceiptSource = PurchasesRepository.HasReceiptSource(purchase),
            SupportsCurrencyEdit = true,
            Owner = owner,
            ItemsDeferred = deferItems,
            IsEdited = snapshot is not null &&
                (snapshot.HeaderChanged(purchase) || snapshot.Items.Length != lines.Count() || lines.Any(LineChanged))
        };
    }
}
