using Microsoft.EntityFrameworkCore;

namespace SupportPoc.TicketService.Data;

/// <summary>
/// SQLite EnsureCreated khong them cot moi — migrate thu cong cho POC.
/// </summary>
internal static class TicketDbSchema
{
    public static async Task EnsureSagaEpochColumnsAsync(TicketDbContext db, CancellationToken cancellationToken = default)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(Tickets)";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }
        finally
        {
            await connection.CloseAsync();
        }

        if (!columns.Contains("SagaEpoch"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN SagaEpoch INTEGER NOT NULL DEFAULT 0",
                cancellationToken);

        if (!columns.Contains("ActiveSagaCorrelationId"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN ActiveSagaCorrelationId TEXT NULL",
                cancellationToken);

        if (!columns.Contains("AiDraftCategory"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN AiDraftCategory TEXT NULL",
                cancellationToken);

        if (!columns.Contains("AiDraftSuggestion"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN AiDraftSuggestion TEXT NULL",
                cancellationToken);

        if (!columns.Contains("AiDraftRelatedDocumentsJson"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN AiDraftRelatedDocumentsJson TEXT NOT NULL DEFAULT '[]'",
                cancellationToken);

        if (!columns.Contains("AiDraftCorrelationId"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN AiDraftCorrelationId TEXT NULL",
                cancellationToken);

        if (!columns.Contains("AiDraftSagaEpoch"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN AiDraftSagaEpoch INTEGER NULL",
                cancellationToken);

        if (!columns.Contains("SagaStopNote"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN SagaStopNote TEXT NULL",
                cancellationToken);

        if (!columns.Contains("OwnerOid"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN OwnerOid TEXT NULL",
                cancellationToken);
    }
}
