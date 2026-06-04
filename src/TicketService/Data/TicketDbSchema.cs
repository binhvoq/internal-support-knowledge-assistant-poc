using Microsoft.EntityFrameworkCore;

namespace SupportPoc.TicketService.Data;

internal static class TicketDbSchema
{
    public static async Task EnsureSchemaAsync(TicketDbContext db, CancellationToken cancellationToken = default)
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

        if (!columns.Contains("OwnerOid"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Tickets ADD COLUMN OwnerOid TEXT NULL",
                cancellationToken);
    }
}
