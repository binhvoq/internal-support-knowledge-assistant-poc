using Microsoft.EntityFrameworkCore;

namespace SupportPoc.AiOrchestrator.Data;

internal static class OrchestratorDbSchema
{
    public static async Task EnsureSagaStateColumnsAsync(OrchestratorDbContext db, CancellationToken cancellationToken = default)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(TicketSuggestionStates)";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }
        finally
        {
            await connection.CloseAsync();
        }

        if (!columns.Contains("TicketSagaEpoch"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE TicketSuggestionStates ADD COLUMN TicketSagaEpoch INTEGER NOT NULL DEFAULT 0",
                cancellationToken);

        await AddColumnIfMissingAsync(db, columns, "VerifyTimeoutTokenId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "PendingTimeoutOutcome", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "TimeoutDecisionReason", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "TimeoutVerifyAttempts", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "PostResendVerifyAttempts", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "MarkResendIssued", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "MarkResendIssuedAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "SaveResendIssued", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "SaveResendIssuedAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "AiRunResendCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "AiRunResendIssuedAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "CompensateResendCount", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, columns, "CompensateResendIssuedAt", "TEXT NULL", cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        OrchestratorDbContext db,
        HashSet<string> columns,
        string name,
        string sqlType,
        CancellationToken cancellationToken)
    {
        if (columns.Contains(name))
            return;

        // Column names/types are constants controlled by this schema helper.
#pragma warning disable EF1003
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE TicketSuggestionStates ADD COLUMN " + name + " " + sqlType,
            cancellationToken);
#pragma warning restore EF1003
        columns.Add(name);
    }
}
