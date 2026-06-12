using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Saga;

namespace SupportPoc.AiOrchestrator.Data;

internal static class OrchestratorSchemaPatcher
{
    public static async Task ApplyAsync(OrchestratorDbContext db, CancellationToken cancellationToken = default)
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
