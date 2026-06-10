using Microsoft.EntityFrameworkCore;

namespace SupportPoc.TicketService.Data;

internal static class TicketDbSchema
{
    public static async Task EnsureSchemaAsync(TicketDbContext db, CancellationToken cancellationToken = default)
    {
        var columns = await GetColumnsAsync(db, "Tickets", cancellationToken);

        if (!columns.Contains("OwnerOid"))
        {
#pragma warning disable EF1003
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN OwnerOid TEXT NULL",
                cancellationToken);
#pragma warning restore EF1003
        }

        if (!columns.Contains("Version"))
        {
#pragma warning disable EF1003
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN Version INTEGER NOT NULL DEFAULT 1",
                cancellationToken);
#pragma warning restore EF1003
        }

        if (!await TableExistsAsync(db, "ProcessedCommands", cancellationToken))
        {
#pragma warning disable EF1003
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS ProcessedCommands (
                    CommandId TEXT NOT NULL PRIMARY KEY,
                    TicketId TEXT NOT NULL,
                    JobId TEXT NOT NULL,
                    Accepted INTEGER NOT NULL,
                    RejectReason TEXT NULL,
                    ProcessedAt TEXT NOT NULL
                );
                """,
                cancellationToken);
#pragma warning restore EF1003
        }

        if (await TableExistsAsync(db, "ProcessedCommands", cancellationToken))
        {
            var processedColumns = await GetColumnsAsync(db, "ProcessedCommands", cancellationToken);
            if (!processedColumns.Contains("JobId"))
            {
#pragma warning disable EF1003
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE ProcessedCommands ADD COLUMN JobId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';",
                    cancellationToken);
#pragma warning restore EF1003
            }
        }

#pragma warning disable EF1003
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ProcessedCommands_TicketId ON ProcessedCommands (TicketId);",
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_ProcessedCommands_TicketId_JobId ON ProcessedCommands (TicketId, JobId);",
            cancellationToken);
#pragma warning restore EF1003
    }

    private static async Task<bool> TableExistsAsync(
        TicketDbContext db,
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

    private static async Task<HashSet<string>> GetColumnsAsync(
        TicketDbContext db,
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
}
