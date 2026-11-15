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
        var engine = configuration["Database:Engine"]?.Trim().ToUpperInvariant();
        if (engine is null or "SQLITE")
        {
            var path = Path.GetFullPath(configuration["Database:SqlitePath"] ?? "data/castar.sqlite", AppContext.BaseDirectory);
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString();
            services.AddDbContextFactory<CastarDbContext>(options => options.UseSqlite(connectionString));
        }
        else if (engine == "SQLSERVER")
        {
            var connectionString = configuration.GetConnectionString("Castar");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Для SQL Server задайте ConnectionStrings:Castar.");

            services.AddDbContextFactory<CastarDbContext>(options => options.UseSqlServer(connectionString,
                sql => sql.UseCompatibilityLevel(130).EnableRetryOnFailure()));
        }
        else
        {
            throw new InvalidOperationException("Database:Engine должен быть Sqlite или SqlServer.");
        }

        services.AddSingleton<IPurchasesRepository, PurchasesRepository>();
        services.AddSingleton<IItemsRepository, ItemsRepository>();
        services.AddSingleton<IIncomesRepository, IncomesRepository>();
        services.AddSingleton<IUsersRepository, UsersRepository>();
        services.AddSingleton<IPurchasesService, PurchasesService>();
        services.AddSingleton<IItemsService, ItemsService>();
        services.AddSingleton<IIncomesService, IncomesService>();
        services.AddSingleton<IUsersService, UsersService>();
        return services;
    }

    public static async Task InitializeCastarDataAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        var factory = app.Services.GetRequiredService<IDbContextFactory<CastarDbContext>>();
        await using var database = await factory.CreateDbContextAsync(cancellationToken);
        if (database.Database.IsSqlite())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(database.Database.GetDbConnection().DataSource)!);
            await database.Database.EnsureCreatedAsync(cancellationToken);
        }
    }
}
