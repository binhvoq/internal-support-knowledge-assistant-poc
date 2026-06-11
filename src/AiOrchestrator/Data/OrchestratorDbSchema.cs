using Microsoft.EntityFrameworkCore;

namespace SupportPoc.AiOrchestrator.Data;

/// <summary>
/// SQLite EnsureCreated khong them/sua bang moi vao DB cu — tao/sua TicketSuggestionSagas + MassTransit inbox/outbox idempotent.
/// </summary>
internal static class OrchestratorDbSchema
{
    private static readonly string[] MassTransitTables = ["InboxState", "OutboxMessage", "OutboxState"];

    public static async Task EnsureSchemaAsync(OrchestratorDbContext db, CancellationToken cancellationToken = default)
    {
        if (db.Database.IsSqlServer())
            return;

        await ConfigureSqliteConcurrencyAsync(db, cancellationToken);
        await EnsureMassTransitPersistenceTablesAsync(db, cancellationToken);
        await EnsureAiGenerationAttemptsTableAsync(db, cancellationToken);

        if (!await TableExistsAsync(db, "TicketSuggestionSagas", cancellationToken))
        {
#pragma warning disable EF1003
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS TicketSuggestionSagas (
                    CorrelationId TEXT NOT NULL PRIMARY KEY,
                    CurrentState TEXT NOT NULL,
                    RowVersion BLOB NULL,
                    TicketId TEXT NOT NULL,
                    EmployeeId TEXT NOT NULL,
                    Question TEXT NOT NULL,
                    OriginalCategory TEXT NOT NULL,
                    JobId TEXT NOT NULL,
                    CurrentAttemptId TEXT NOT NULL,
                    RetryCount INTEGER NOT NULL DEFAULT 0,
                    TicketVersionAtStart INTEGER NULL,
                    StepTimeoutTokenId TEXT NULL,
                    LastProposeCommandId TEXT NULL,
                    GeneratedCategory TEXT NULL,
                    GeneratedSuggestion TEXT NULL,
                    GeneratedRelatedDocumentsJson TEXT NOT NULL DEFAULT '[]',
                    FailureReason TEXT NULL,
                    DiscardReason TEXT NULL,
                    LateMessageAudit TEXT NULL,
                    PendingReconcileAction TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """,
                cancellationToken);
#pragma warning restore EF1003
        }

#pragma warning disable EF1003
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_TicketSuggestionSagas_TicketId ON TicketSuggestionSagas (TicketId);",
            cancellationToken);
#pragma warning restore EF1003
    }

    private static async Task ConfigureSqliteConcurrencyAsync(
        OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
#pragma warning disable EF1003
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=30000;", cancellationToken);
#pragma warning restore EF1003
    }

    private static async Task EnsureMassTransitPersistenceTablesAsync(
        OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
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

    private static async Task EnsureAiGenerationAttemptsTableAsync(
        OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
        if (await TableExistsAsync(db, "AiGenerationAttempts", cancellationToken))
            return;

#pragma warning disable EF1003
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS AiGenerationAttempts (
                AttemptId TEXT NOT NULL PRIMARY KEY,
                SagaId TEXT NOT NULL,
                JobId TEXT NOT NULL,
                TicketId TEXT NOT NULL,
                Status TEXT NOT NULL,
                Category TEXT NULL,
                Suggestion TEXT NULL,
                RelatedDocumentsJson TEXT NOT NULL DEFAULT '[]',
                Error TEXT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """,
            cancellationToken);
#pragma warning restore EF1003
    }

    private static bool RelatesToOrchestratorPersistence(string statement)
    {
        if (statement.Contains("TicketSuggestionSagas", StringComparison.OrdinalIgnoreCase))
            return true;
        if (statement.Contains("AiGenerationAttempts", StringComparison.OrdinalIgnoreCase))
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
