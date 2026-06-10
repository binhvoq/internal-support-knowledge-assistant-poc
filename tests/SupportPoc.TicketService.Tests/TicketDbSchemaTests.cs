using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Tests;

public sealed class TicketDbSchemaTests
{
    [Fact]
    public async Task EnsureSchema_creates_ProcessedCommands_on_legacy_db_without_table()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ticket-legacy-{Guid.NewGuid():N}.db");
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE Tickets (
                    Id TEXT NOT NULL PRIMARY KEY,
                    EmployeeId TEXT NOT NULL,
                    Category TEXT NOT NULL,
                    Question TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<TicketDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (var db = new TicketDbContext(options))
        {
            await TicketDbSchema.EnsureSchemaAsync(db);
        }

        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='ProcessedCommands'";
        Assert.Equal("ProcessedCommands", await check.ExecuteScalarAsync());

        await using var index = connection.CreateCommand();
        index.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='IX_ProcessedCommands_TicketId'";
        Assert.Equal("IX_ProcessedCommands_TicketId", await index.ExecuteScalarAsync());
    }
}
