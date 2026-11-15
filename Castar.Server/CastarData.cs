using Castar.BL;
using Castar.DAL;
using Castar.DAL.Repositories.DataBase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

public static class CastarData
{
    public static IServiceCollection AddCastarData(this IServiceCollection services, IConfiguration configuration)
    {
        var engine = configuration["Database:Engine"]?.Trim();
        if (!string.IsNullOrEmpty(engine) && !engine.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Castar поддерживает только SQLite. Укажите путь к перенесённой базе в Database:SqlitePath и удалите Database:Engine.");
        }
        var path = GetSqlitePath(configuration);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
        services.AddDbContext<CastarDbContext>(options => options.UseSqlite(connectionString));

        services.AddScoped<IPurchasesRepository, PurchasesRepository>();
        services.AddScoped<IItemsRepository, ItemsRepository>();
        services.AddScoped<IIncomesRepository, IncomesRepository>();
        services.AddScoped<IUsersRepository, UsersRepository>();
        services.AddScoped<IPurchasesService, PurchasesService>();
        services.AddScoped<IItemsService, ItemsService>();
        services.AddScoped<IIncomesService, IncomesService>();
        services.AddScoped<IUsersService, UsersService>();
        return services;
    }

    internal static string GetSqlitePath(IConfiguration configuration) =>
        Path.GetFullPath(configuration["Database:SqlitePath"] ?? "data/castar.sqlite", AppContext.BaseDirectory);

    public static async Task InitializeCastarDataAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<CastarDbContext>();
        Directory.CreateDirectory(Path.GetDirectoryName(database.Database.GetDbConnection().DataSource)!);
        await CastarSqliteSchema.MigrateAsync(database, cancellationToken);
    }
}
