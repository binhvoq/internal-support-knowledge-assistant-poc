using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Data;

internal static class OrchestratorSchemaPatcher
{
    public static async Task ApplyAsync(OrchestratorDbContext db, CancellationToken cancellationToken = default)
    {
        await PatchTicketSuggestionSagaAsync(db, cancellationToken);
        await PatchAiGenerationAttemptsAsync(db, cancellationToken);
    }

    private static async Task PatchTicketSuggestionSagaAsync(OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        var entityType = db.Model.FindEntityType(typeof(TicketSuggestionSaga));
        if (entityType is null)
            return;

        var table = entityType.GetTableName();
        if (string.IsNullOrWhiteSpace(table))
            return;

        var schema = entityType.GetSchema() ?? "dbo";
        var sql = BuildAddColumnIfMissingSql(schema, table, "ProposeRetryCount", "int NOT NULL DEFAULT 0");
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task PatchAiGenerationAttemptsAsync(OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        var entityType = db.Model.FindEntityType(typeof(AiGenerationAttemptEntity));
        if (entityType is null)
            return;

        var table = entityType.GetTableName();
        if (string.IsNullOrWhiteSpace(table))
            return;

        var schema = entityType.GetSchema() ?? "dbo";
        var statements = new[]
        {
            BuildAddColumnIfMissingSql(schema, table, "Question", "nvarchar(4000) NOT NULL DEFAULT ''"),
            BuildAddColumnIfMissingSql(schema, table, "RequestedCategory", "nvarchar(32) NOT NULL DEFAULT ''"),
            BuildAddColumnIfMissingSql(schema, table, "LeaseOwner", "nvarchar(64) NULL"),
            BuildAddColumnIfMissingSql(schema, table, "LeaseUntil", "datetimeoffset NULL"),
            BuildAddColumnIfMissingSql(schema, table, "RetryCount", "int NOT NULL DEFAULT 0"),
            BuildAddColumnIfMissingSql(schema, table, "NextRunAt", "datetimeoffset NULL"),
            BuildAddRowVersionColumnIfMissingSql(schema, table)
        };

        foreach (var sql in statements)
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    internal static string BuildAddRowVersionColumnIfMissingSql(string schema, string table)
    {
        var qualifiedName = $"{schema}.{table}";
        var qualifiedTable = $"[{schema}].[{table}]";
        return $"""
            IF OBJECT_ID(N'{qualifiedName}', N'U') IS NOT NULL
            AND COL_LENGTH(N'{qualifiedName}', N'RowVersion') IS NULL
            BEGIN
                ALTER TABLE {qualifiedTable} ADD [RowVersion] rowversion NOT NULL;
            END
            """;
    }

    internal static string BuildAddColumnIfMissingSql(
        string schema,
        string table,
        string column,
        string columnDefinition)
    {
        var qualifiedName = $"{schema}.{table}";
        var qualifiedTable = $"[{schema}].[{table}]";
        return $"""
            IF OBJECT_ID(N'{qualifiedName}', N'U') IS NOT NULL
            AND COL_LENGTH(N'{qualifiedName}', N'{column}') IS NULL
            BEGIN
                ALTER TABLE {qualifiedTable} ADD [{column}] {columnDefinition};
            END
            """;
    }
}
