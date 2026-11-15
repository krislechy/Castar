using System.Data.Common;
using Castar.DAL;

namespace Castar.Server;

public static class CastarDatabaseCommands
{
    public static async Task<int?> TryRunAsync(
        string[] args, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] != "import-sqlite")
            return null;

        var source = configuration.GetConnectionString("Castar");
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]) || string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("Использование: Castar.Server import-sqlite <новый файл>. Задайте ConnectionStrings__Castar.");
            return 2;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void Cancel(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }

        Console.CancelKeyPress += Cancel;
        try
        {
            var counts = await CastarSqliteImporter.ImportAsync(source, args[1], cancellation.Token);
            Console.WriteLine($"Перенесено: пользователей {counts.Users}, покупок {counts.Purchases}, " +
                $"позиций {counts.Items}, начислений {counts.Incomes}.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Перенос отменён.");
            return 1;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or DbException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine("Перенос не выполнен. Проверьте подключение к совместимой базе SQL Server, " +
                "права записи и убедитесь, что целевой файл ещё не существует.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }
    }
}
