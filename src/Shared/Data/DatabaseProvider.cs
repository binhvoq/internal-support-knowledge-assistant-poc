using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SupportPoc.Shared.Data;

public static class DatabaseProvider
{
    public static bool IsSqlServer(string? connectionString) =>
        !string.IsNullOrWhiteSpace(connectionString)
        && connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase);

    public static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString)
    {
        if (IsSqlServer(connectionString))
            options.UseSqlServer(connectionString);
        else
            options.UseSqlite(connectionString);
    }

    public static async Task EnsureDatabaseReadyAsync(
        DbContext db,
        CancellationToken cancellationToken = default)
    {
        var connectionString = db.Database.GetConnectionString();
        if (IsSqlServer(connectionString) && !string.IsNullOrWhiteSpace(connectionString))
            await EnsureSqlServerDatabaseExistsAsync(connectionString, cancellationToken);

        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public static async Task EnsureSqlServerDatabaseExistsAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
            return;

        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID(@db) IS NULL
            BEGIN
                EXEC('CREATE DATABASE [' + @db + ']');
            END
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@db";
        parameter.Value = databaseName;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
