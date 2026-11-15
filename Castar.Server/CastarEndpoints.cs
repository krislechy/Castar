using Castar.Api;
using Castar.BL;
using Castar.DAL.Repositories.DataBase;
using Castar.Domain.Models.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static class CastarEndpoints
{
    private static readonly SemaphoreSlim UserWrites = new(1);

    public static RouteGroupBuilder MapCastarApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        api.AddEndpointFilter(HandleDatabaseConflicts);
        api.MapGet("/settings", (ServerSettings settings) => Results.Ok(settings.Read()));
        api.MapPut("/settings", (ServerSettings settings, ServerSettingsWriteDto requested, CancellationToken token) =>
            settings.UpdateAsync(requested, token));
        api.MapGet("/users", GetUsers);
        api.MapGet("/users/{id:guid}", GetUser);
        api.MapPost("/users", CreateUser).AddEndpointFilter(SerializeUserWrites);
        api.MapPut("/users/{id:guid}", UpdateUser).AddEndpointFilter(SerializeUserWrites);
        api.MapDelete("/users/{id:guid}", DeleteUser);

        api.MapGet("/incomes", GetIncomes);
        api.MapGet("/incomes/{id:guid}", GetIncome);
        api.MapPost("/incomes", CreateIncome);
        api.MapPut("/incomes/{id:guid}", UpdateIncome);
        api.MapDelete("/incomes/{id:guid}", DeleteIncome);

        api.MapGet("/purchases", GetPurchases);
        return api;
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

    private static async Task<IResult> CreateUser(UserWriteDto input, IUsersService users, CancellationToken cancellationToken)
    {
        if (Validate(input) is { } invalid)
            return invalid;
        if (await IsTelegramIdAssigned(input.TelegramId, users, cancellationToken))
            return DuplicateTelegramId();

        var user = new User();
        Apply(user, input);
        await users.Add(user);
        return Results.Created($"/api/users/{user.Id}", ToDto(user));
    }

    private static async Task<IResult> UpdateUser(Guid id, UserWriteDto input, IUsersRepository repository,
        IUsersService users, CancellationToken cancellationToken)
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
        return Results.Ok(ToDto(user));
    }

    private static async Task<IResult> DeleteUser(Guid id, IUsersService users) =>
        await users.Delete(id) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> GetIncomes(DateTime start, DateTime finish, IIncomesService incomes)
    {
        if (finish < start)
            return InvalidInput("Дата окончания должна быть не раньше даты начала.");

        var entries = await incomes.GetEntitiesWithAllIncludes(start, finish);
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
        IUsersRepository users, CancellationToken cancellationToken)
    {
        if (Validate(input) is { } invalid)
            return invalid;

        var owner = await users.Find(input.UserId, cancellationToken);
        if (owner is null)
            return InvalidInput("Выберите существующего пользователя.");

        var income = new Income();
        Apply(income, input, owner);
        await incomes.Add(income, owner.TelegramId, owner);
        return Results.Created($"/api/incomes/{income.Id}", ToDto(income));
    }

    private static async Task<IResult> UpdateIncome(Guid id, IncomeWriteDto input, IIncomesRepository repository,
        IIncomesService incomes, IUsersRepository users, CancellationToken cancellationToken)
    {
        if (Validate(input) is { } invalid)
            return invalid;

        var income = await repository.Find(id, cancellationToken);
        if (income is null)
            return Results.NotFound();

        var owner = await users.Find(input.UserId, cancellationToken);
        if (owner is null)
            return InvalidInput("Выберите существующего пользователя.");

        Apply(income, input, owner);
        await incomes.Update(income);
        return Results.Ok(ToDto(income));
    }

    private static async Task<IResult> DeleteIncome(Guid id, IIncomesService incomes) =>
        await incomes.Delete(id) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> GetPurchases(DateTime start, DateTime finish, IPurchasesService purchases)
    {
        if (finish < start)
            return InvalidInput("Дата окончания должна быть не раньше даты начала.");

        var entries = await purchases.GetEntitiesWithAllIncludes(start, finish);
        return Results.Ok(entries.Select(ToDto).ToArray());
    }

    private static IResult? Validate(UserWriteDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200)
            return InvalidInput("Укажите имя пользователя длиной до 200 символов.");
        if (input.Sex is not (0 or 1) || input.ColorRaw is not { Length: 4 })
            return InvalidInput("Проверьте пол и цвет пользователя.");

        return null;
    }

    private static IResult? Validate(IncomeWriteDto input)
    {
        const decimal amountLimit = 10000000000000000000000m;
        var amount = decimal.Round(input.Sum, 2, MidpointRounding.AwayFromZero);
        if (amount <= -amountLimit || amount >= amountLimit || amount == 0)
            return InvalidInput("Укажите ненулевую сумму с точностью до копеек.");
        if (input.AddedOn < new DateTime(1753, 1, 1) || input.AddedOn > new DateTime(9999, 12, 31, 23, 59, 59, 997))
            return InvalidInput("Укажите допустимую дату начисления.");
        if (string.IsNullOrWhiteSpace(input.Source) || input.Source.Length > 400)
            return InvalidInput("Укажите источник начисления длиной до 400 символов.");

        return null;
    }

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
    }

    private static void Apply(Income income, IncomeWriteDto input, User owner)
    {
        income.User = owner;
        income.Source = input.Source;
        income.Sum = input.Sum;
        income.AddedOn = DateTime.SpecifyKind(input.AddedOn, DateTimeKind.Unspecified);
    }

    private static UserDto ToDto(User user) =>
        new(user.Id, user.TelegramId, user.Name, user.Sex, user.ColorRaw, user.ImageRaw);

    private static IncomeDto ToDto(Income income) =>
        new(income.Id, income.UserId, ToDto(income.User) with { ImageRaw = null }, income.MessageId,
            income.Sum, income.Source, income.AddedOn);

    private static PurchaseDto ToDto(Purchase purchase) =>
        new(purchase.Id, purchase.UserId, purchase.Name, purchase.TotalSum, purchase.Currency, purchase.MessageId,
            purchase.AddedOn, purchase.RetailPlace, purchase.RetailPlaceAddress,
            purchase.Items.Select(item => new PurchaseItemDto(
                item.Id, item.Name, item.Category, item.Quantity, item.Price, item.Sum)).ToArray());
}
