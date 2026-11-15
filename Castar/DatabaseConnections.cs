using Castar.Domain.Models.SettingsModel;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using System;
using System.IO;

namespace Castar
{
    internal static class DatabaseConnections
    {
        public static string DataDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Castar");

        public static string GetSqlitePath(DataBase settings) => string.IsNullOrWhiteSpace(settings.SqlitePath)
            ? Path.Combine(DataDirectory, "castar.sqlite")
            : Path.GetFullPath(settings.SqlitePath, DataDirectory);

        public static string GetSqliteConnectionString(DataBase settings) =>
            new SqliteConnectionStringBuilder
            {
                DataSource = GetSqlitePath(settings),
                ForeignKeys = true
            }.ConnectionString;

        public static string GetSqlServerConnectionString(DataBase settings)
        {
            var connection = new SqlConnectionStringBuilder
            {
                DataSource = settings.DataSource.Trim(),
                InitialCatalog = settings.DatabaseName.Trim(),
                AttachDBFilename = settings.AttachDBFileName.Trim(),
                IntegratedSecurity = string.IsNullOrWhiteSpace(settings.Login),
                TrustServerCertificate = settings.TrustServerCertificate
            };
            if (connection.InitialCatalog.Length == 0 && connection.AttachDBFilename.Length == 0)
                connection.InitialCatalog = "Castar";
            if (!connection.IntegratedSecurity)
            {
                connection.UserID = settings.Login;
                connection.Password = settings.Password ?? "";
            }
            return connection.ConnectionString;
        }
    }
}
