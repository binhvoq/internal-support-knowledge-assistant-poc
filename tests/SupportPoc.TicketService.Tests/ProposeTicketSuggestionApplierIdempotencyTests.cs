using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class ProposeTicketSuggestionApplierIdempotencyTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TicketDbContext _db;
    private readonly ProposeTicketSuggestionApplier _applier;

    public ProposeTicketSuggestionApplierIdempotencyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<TicketDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new TicketDbContext(options);
        _db.Database.EnsureCreated();
        _applier = new ProposeTicketSuggestionApplier(_db, NullLogger<ProposeTicketSuggestionApplier>.Instance);
    }

    [Fact]
    public async Task Duplicate_CommandId_does_not_apply_ticket_twice()
    {
        var commandId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await SeedTicketAsync(TestTicketIds.Default, TicketStatus.New, version: 1);

        var msg = CreateMessage(commandId, jobId, TestTicketIds.Default, expectedVersion: 1);
        var first = await _applier.ApplyAsync(msg);
        var second = await _applier.ApplyAsync(msg);

        Assert.True(first.Accepted);
        Assert.True(second.Accepted);
        Assert.Equal(1, await _db.ProcessedCommands.CountAsync());

        var ticket = await ReloadTicketAsync();
        Assert.Equal(TicketStatus.Suggested, ticket.Status);
        Assert.Equal(2, ticket.Version);
        Assert.Equal("suggested answer", ticket.AiSuggestedAnswer);
    }

    [Fact]
    public async Task Different_CommandId_from_other_job_does_not_mutate_ticket_again()
    {
        await SeedTicketAsync(TestTicketIds.Second, TicketStatus.New, version: 1);

        var accepted = await _applier.ApplyAsync(
            CreateMessage(Guid.NewGuid(), Guid.NewGuid(), TestTicketIds.Second, expectedVersion: 1));
        var replay = await _applier.ApplyAsync(
            CreateMessage(Guid.NewGuid(), Guid.NewGuid(), TestTicketIds.Second, expectedVersion: 2));

        Assert.True(accepted.Accepted);
        Assert.False(replay.Accepted);
        Assert.Equal(2, await _db.ProcessedCommands.CountAsync());

        var ticket = await ReloadTicketAsync();
        Assert.Equal(2, ticket.Version);
        Assert.Equal("suggested answer", ticket.AiSuggestedAnswer);
    }

    private async Task<TicketEntity> ReloadTicketAsync()
    {
        _db.ChangeTracker.Clear();
        return await _db.Tickets.AsNoTracking().SingleAsync();
    }

    private async Task SeedTicketAsync(string id, string status, long version)
    {
        var now = DateTimeOffset.UtcNow;
        _db.Tickets.Add(new TicketEntity
        {
            Id = id,
            EmployeeId = "emp@test",
            Category = SupportCategory.Other,
            Question = "question?",
            Status = status,
            Version = version,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _db.SaveChangesAsync();
    }

    private static ProposeTicketSuggestion CreateMessage(
        Guid commandId,
        Guid jobId,
        string ticketId,
        long? expectedVersion) =>
        new(
            commandId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            jobId,
            ticketId,
            SupportCategory.IT,
            "suggested answer",
            [],
            expectedVersion);

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
