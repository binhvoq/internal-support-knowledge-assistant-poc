using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Data;

internal static class OrchestratorSchemaPatcher
{
    public static async Task ApplyAsync(OrchestratorDbContext db, CancellationToken cancellationToken = default)
    {
        await PatchTicketSuggestionSagaAsync(db, cancellationToken);
        await PatchAiGenerationAttemptsAsync(db, cancellationToken);
        await PatchSagaReconciliationItemsAsync(db, cancellationToken);
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
        var statements = new[]
        {
            BuildAddColumnIfMissingSql(schema, table, "ProposeRetryCount", "int NOT NULL DEFAULT 0"),
            BuildAddColumnIfMissingSql(schema, table, "ReconcileTransientFailureCount", "int NOT NULL DEFAULT 0"),
            BuildAddColumnIfMissingSql(schema, table, "LastReconcileAttemptAt", "datetimeoffset NULL"),
            BuildAddColumnIfMissingSql(schema, table, "ReconcilingSinceAt", "datetimeoffset NULL")
        };

        foreach (var sql in statements)
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

    private static async Task PatchSagaReconciliationItemsAsync(OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        var qualifiedTable = "[dbo].[SagaReconciliationItems]";
        var createSql = $"""
            IF OBJECT_ID(N'dbo.SagaReconciliationItems', N'U') IS NULL
            BEGIN
                CREATE TABLE {qualifiedTable} (
                    [SagaId] uniqueidentifier NOT NULL,
                    [TicketId] nvarchar(32) NOT NULL,
                    [JobId] uniqueidentifier NOT NULL,
                    [Reason] nvarchar(2000) NOT NULL,
                    [CreatedAt] datetimeoffset NOT NULL,
                    [LastAttemptAt] datetimeoffset NULL,
                    [AttemptCount] int NOT NULL DEFAULT 0,
                    [ResolvedAt] datetimeoffset NULL,
                    [Resolution] nvarchar(64) NULL,
                    CONSTRAINT [PK_SagaReconciliationItems] PRIMARY KEY ([SagaId])
                );
                CREATE INDEX [IX_SagaReconciliationItems_ResolvedAt] ON {qualifiedTable} ([ResolvedAt]);
            END
            """;
        await db.Database.ExecuteSqlRawAsync(createSql, cancellationToken);
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
