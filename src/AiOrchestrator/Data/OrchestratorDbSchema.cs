using Microsoft.EntityFrameworkCore;

namespace SupportPoc.AiOrchestrator.Data;

/// <summary>
/// SQLite EnsureCreated khong them/sua bang moi vao DB cu (saga). Tao/sua AutoSuggestionJobs + MassTransit inbox/outbox idempotent.
/// </summary>
internal static class OrchestratorDbSchema
{
    private static readonly string[] MassTransitTables = ["InboxState", "OutboxMessage", "OutboxState"];

    private static readonly (string Name, string SqlType)[] AlterColumns =
    [
        ("EmployeeId", "TEXT NOT NULL DEFAULT ''"),
        ("Question", "TEXT NOT NULL DEFAULT ''"),
        ("Category", "TEXT NOT NULL DEFAULT 'Other'"),
        ("Status", "TEXT NOT NULL DEFAULT 'Running'"),
        ("ProducedCategory", "TEXT NULL"),
        ("ProducedSuggestion", "TEXT NULL"),
        ("ProducedRelatedDocumentsJson", "TEXT NOT NULL DEFAULT '[]'"),
        ("FailureReason", "TEXT NULL"),
        ("DiscardReason", "TEXT NULL"),
        ("CreatedAt", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00+00:00'"),
        ("UpdatedAt", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00+00:00'"),
    ];

    public static async Task EnsureSchemaAsync(OrchestratorDbContext db, CancellationToken cancellationToken = default)
    {
        await EnsureMassTransitPersistenceTablesAsync(db, cancellationToken);

        if (!await TableExistsAsync(db, "AutoSuggestionJobs", cancellationToken))
        {
#pragma warning disable EF1003
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS AutoSuggestionJobs (
                    JobId TEXT NOT NULL PRIMARY KEY,
                    TicketId TEXT NOT NULL,
                    EmployeeId TEXT NOT NULL,
                    Question TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    ProducedCategory TEXT NULL,
                    ProducedSuggestion TEXT NULL,
                    ProducedRelatedDocumentsJson TEXT NOT NULL DEFAULT '[]',
                    FailureReason TEXT NULL,
                    DiscardReason TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """,
                cancellationToken);
#pragma warning restore EF1003
        }
        else
        {
            var columns = await GetColumnsAsync(db, "AutoSuggestionJobs", cancellationToken);
            foreach (var (name, sqlType) in AlterColumns)
            {
                if (columns.Contains(name))
                    continue;

#pragma warning disable EF1003
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE AutoSuggestionJobs ADD COLUMN " + name + " " + sqlType,
                    cancellationToken);
#pragma warning restore EF1003
            }
        }

#pragma warning disable EF1003
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_AutoSuggestionJobs_TicketId ON AutoSuggestionJobs (TicketId);",
            cancellationToken);
#pragma warning restore EF1003
    }

    /// <summary>DB saga cu co bang khac nhung thieu InboxState/Outbox* — EnsureCreated khong bo sung.</summary>
    private static async Task EnsureMassTransitPersistenceTablesAsync(
        OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
        // Khong return som khi chi co InboxState — legacy co the thieu OutboxMessage/OutboxState.
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        await using var template = new OrchestratorDbContext(options);
        await template.Database.EnsureCreatedAsync(cancellationToken);
        var script = template.Database.GenerateCreateScript();

        foreach (var statement in SplitSqlStatements(script))
        {
            var isTable = statement.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase);
            var isIndex = statement.Contains("CREATE INDEX", StringComparison.OrdinalIgnoreCase)
                || statement.Contains("CREATE UNIQUE INDEX", StringComparison.OrdinalIgnoreCase);
            if (!isTable && !isIndex)
                continue;

            if (!RelatesToOrchestratorPersistence(statement))
                continue;

            if (isTable)
            {
                var table = ExtractSqliteTableName(statement);
                if (table is not null && await TableExistsAsync(db, table, cancellationToken))
                    continue;
            }

            var idempotent = statement
                .Replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", StringComparison.OrdinalIgnoreCase)
                .Replace("CREATE TABLE ", "CREATE TABLE IF NOT EXISTS ", StringComparison.OrdinalIgnoreCase)
                .Replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", StringComparison.OrdinalIgnoreCase);

#pragma warning disable EF1003
            await db.Database.ExecuteSqlRawAsync(idempotent, cancellationToken);
#pragma warning restore EF1003
        }
    }

    private static IEnumerable<string> SplitSqlStatements(string script)
    {
        foreach (var part in script.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
                yield return part;
        }
    }

    private static bool RelatesToOrchestratorPersistence(string statement)
    {
        if (statement.Contains("AutoSuggestionJobs", StringComparison.OrdinalIgnoreCase))
            return true;
        return MassTransitTables.Any(t => statement.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ExtractSqliteTableName(string statement)
    {
        var tokens = statement.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            if (tokens[i].Equals("TABLE", StringComparison.OrdinalIgnoreCase)
                || tokens[i].Equals("INDEX", StringComparison.OrdinalIgnoreCase))
            {
                var name = tokens[i + 1].Trim('"', '[', ']');
                if (name.Equals("IF", StringComparison.OrdinalIgnoreCase) && i + 2 < tokens.Length)
                    name = tokens[i + 2].Trim('"', '[', ']');
                return name;
            }
        }

        return null;
    }

    private static async Task<HashSet<string>> GetColumnsAsync(
        OrchestratorDbContext db,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(" + tableName + ")";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }
        finally
        {
            await connection.CloseAsync();
        }

        return columns;
    }

    private static async Task<bool> TableExistsAsync(
        OrchestratorDbContext db,
        string tableName,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
            var param = cmd.CreateParameter();
            param.ParameterName = "$name";
            param.Value = tableName;
            cmd.Parameters.Add(param);
            return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
