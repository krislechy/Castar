using System.Net;
using Castar.DAL;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Castar.Server;

internal static class DatabaseBackup
{
    internal const string ContentType = "application/vnd.sqlite3";

    internal static async Task DownloadAsync(CastarDbContext database, HttpContext context, ILoggerFactory loggerFactory)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress?.IsIPv4MappedToIPv6 == true)
            remoteAddress = remoteAddress.MapToIPv4();
        if (!context.Request.IsHttps && (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress)))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                detail: "Скачивайте резервную копию через HTTPS. Для локального сервера доступен HTTP.").ExecuteAsync(context);
            return;
        }

        var cancellationToken = context.RequestAborted;
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = Directory.CreateTempSubdirectory("castar-backup-");
        try
        {
            var path = Path.Combine(temporary.FullName, "castar.sqlite");
            var source = (SqliteConnection)database.Database.GetDbConnection();
            var destination = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
            await database.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                using var snapshot = new SqliteConnection(destination.ConnectionString);
                source.BackupDatabase(snapshot);
            }
            finally
            {
                await database.Database.CloseConnectionAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            context.Response.Headers.CacheControl = "no-store";
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await Results.File(stream, ContentType, $"Castar-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.sqlite").ExecuteAsync(context);
        }
        finally
        {
            try
            {
                temporary.Delete(recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                loggerFactory.CreateLogger("Castar.Server.DatabaseBackup")
                    .LogWarning("Could not remove the temporary SQLite snapshot ({ErrorType}).", error.GetType().Name);
            }
        }
    }
}
