using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace SupportPoc.Shared.Data;

public static class DatabaseProvider
{
    public const string DefaultTicketsConnection =
        "Server=localhost,1433;Database=supportpoc_tickets;User Id=sa;Password=SupportPoc_LocalSql1!;TrustServerCertificate=True;";

    public const string DefaultOrchestratorConnection =
        "Server=localhost,1433;Database=supportpoc_orchestrator;User Id=sa;Password=SupportPoc_LocalSql1!;TrustServerCertificate=True;";

    public const string DefaultKnowledgeConnection =
        "Server=localhost,1433;Database=supportpoc_knowledge;User Id=sa;Password=SupportPoc_LocalSql1!;TrustServerCertificate=True;";

    public static void ConfigureDbContext(DbContextOptionsBuilder options, string connectionString) =>
        options.UseSqlServer(connectionString);

    public static async Task EnsureDatabaseReadyAsync(
        DbContext db,
        CancellationToken cancellationToken = default)
    {
        var connectionString = db.Database.GetConnectionString();
        if (!string.IsNullOrWhiteSpace(connectionString))
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
