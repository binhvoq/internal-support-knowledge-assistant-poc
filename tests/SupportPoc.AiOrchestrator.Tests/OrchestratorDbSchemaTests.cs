using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Data;

namespace SupportPoc.AiOrchestrator.Tests;

public sealed class OrchestratorDbSchemaTests
{
    [Fact]
    public async Task EnsureSchema_creates_AutoSuggestionJobs_on_legacy_db_without_table()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"orch-legacy-{Guid.NewGuid():N}.db");
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE TicketSuggestionStates (
                    CorrelationId TEXT NOT NULL PRIMARY KEY
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (var db = new OrchestratorDbContext(options))
        {
            await OrchestratorDbSchema.EnsureSchemaAsync(db);
        }

        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='AutoSuggestionJobs'";
        var name = await check.ExecuteScalarAsync();
        Assert.Equal("AutoSuggestionJobs", name);

        await using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(AutoSuggestionJobs)";
        var columns = new List<string>();
        await using var reader = await info.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));
        Assert.Contains("JobId", columns);
        Assert.Contains("TicketId", columns);
        Assert.Contains("Status", columns);
    }

    [Fact]
    public async Task EnsureSchema_creates_inbox_outbox_on_legacy_db_without_mass_transit_tables()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"orch-legacy-mt-{Guid.NewGuid():N}.db");
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE TicketSuggestionStates (
                    CorrelationId TEXT NOT NULL PRIMARY KEY
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (var db = new OrchestratorDbContext(options))
        {
            await OrchestratorDbSchema.EnsureSchemaAsync(db);
        }

        foreach (var table in new[] { "InboxState", "OutboxMessage", "OutboxState", "AutoSuggestionJobs" })
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
            check.Parameters.AddWithValue("$name", table);
            Assert.NotNull(await check.ExecuteScalarAsync());
        }
    }

    [Fact]
    public async Task EnsureSchema_repairs_missing_outbox_when_inbox_already_exists()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"orch-partial-mt-{Guid.NewGuid():N}.db");
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE TicketSuggestionStates (CorrelationId TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE InboxState (MessageId TEXT NOT NULL PRIMARY KEY, ConsumerId TEXT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using (var db = new OrchestratorDbContext(options))
        {
            await OrchestratorDbSchema.EnsureSchemaAsync(db);
        }

        foreach (var table in new[] { "OutboxMessage", "OutboxState" })
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
            check.Parameters.AddWithValue("$name", table);
            Assert.NotNull(await check.ExecuteScalarAsync());
        }
    }
}
