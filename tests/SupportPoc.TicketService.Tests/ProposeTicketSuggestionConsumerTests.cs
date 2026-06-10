using MassTransit;
using MassTransit.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Consumers;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

public sealed class ProposeTicketSuggestionConsumerTests
{
    [Fact]
    public async Task Accepts_proposal_when_ticket_is_new()
    {
        var commandId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTicketAsync(provider, "TCK-OK", TicketStatus.New, version: 1);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var client = harness.GetRequestClient<IProposeTicketSuggestion>();
        var response = await client.GetResponse<IProposeTicketSuggestionResult>(
            new ProposeTicketSuggestion(
                commandId,
                sagaId,
                attemptId,
                jobId,
                "TCK-OK",
                SupportCategory.IT,
                "suggestion",
                [],
                1));

        Assert.True(response.Message.Accepted);

        await using var scope = provider.CreateAsyncScope();
        var ticket = await scope.ServiceProvider.GetRequiredService<TicketDbContext>().Tickets.FindAsync("TCK-OK");
        Assert.Equal(TicketStatus.Suggested, ticket!.Status);
        Assert.Equal("suggestion", ticket.AiSuggestedAnswer);
        Assert.Equal(2, ticket.Version);

        var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
        var processed = await db.ProcessedCommands.FindAsync(commandId);
        Assert.NotNull(processed);
        Assert.True(processed!.Accepted);
    }

    [Fact]
    public async Task Rejects_when_agent_already_resolved()
    {
        var commandId = Guid.NewGuid();
        var sagaId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTicketAsync(provider, "TCK-RES", TicketStatus.Resolved, finalAnswer: "agent", version: 3);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var client = harness.GetRequestClient<IProposeTicketSuggestion>();
        var response = await client.GetResponse<IProposeTicketSuggestionResult>(
            new ProposeTicketSuggestion(
                commandId,
                sagaId,
                attemptId,
                jobId,
                "TCK-RES",
                SupportCategory.IT,
                "late",
                [],
                3));

        Assert.False(response.Message.Accepted);
        Assert.Contains("final answer", response.Message.Reason ?? "", StringComparison.OrdinalIgnoreCase);

        await using var scope = provider.CreateAsyncScope();
        var ticket = await scope.ServiceProvider.GetRequiredService<TicketDbContext>().Tickets.FindAsync("TCK-RES");
        Assert.Equal(TicketStatus.Resolved, ticket!.Status);
        Assert.Null(ticket.AiSuggestedAnswer);
    }

    [Fact]
    public async Task Rejects_when_ticket_version_mismatch()
    {
        var commandId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTicketAsync(provider, "TCK-VER", TicketStatus.New, version: 5);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var client = harness.GetRequestClient<IProposeTicketSuggestion>();
        var response = await client.GetResponse<IProposeTicketSuggestionResult>(
            new ProposeTicketSuggestion(
                commandId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "TCK-VER",
                SupportCategory.IT,
                "stale",
                [],
                1));

        Assert.False(response.Message.Accepted);
        Assert.Contains("version mismatch", response.Message.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_when_different_job_proposes_after_ticket_already_suggested()
    {
        var firstJobId = Guid.NewGuid();
        var secondJobId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTicketAsync(provider, "TCK-DUP", TicketStatus.New, version: 1);

        await using var scope = provider.CreateAsyncScope();
        var applier = scope.ServiceProvider.GetRequiredService<ProposeTicketSuggestionApplier>();

        var first = await applier.ApplyAsync(new ProposeTicketSuggestion(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            firstJobId,
            "TCK-DUP",
            SupportCategory.IT,
            "first suggestion",
            [],
            1));
        Assert.True(first.Accepted);

        var second = await applier.ApplyAsync(new ProposeTicketSuggestion(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            secondJobId,
            "TCK-DUP",
            SupportCategory.IT,
            "late suggestion",
            [],
            2));
        Assert.False(second.Accepted);
        Assert.Contains("already has an accepted AI suggestion", second.RejectReason ?? "", StringComparison.OrdinalIgnoreCase);

        var ticket = await scope.ServiceProvider.GetRequiredService<TicketDbContext>().Tickets.FindAsync("TCK-DUP");
        Assert.Equal("first suggestion", ticket!.AiSuggestedAnswer);
    }

    [Fact]
    public async Task Idempotent_accept_when_same_job_replays_with_new_command_id()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTicketAsync(provider, "TCK-JOB", TicketStatus.New, version: 1);

        await using var scope = provider.CreateAsyncScope();
        var applier = scope.ServiceProvider.GetRequiredService<ProposeTicketSuggestionApplier>();

        var first = await applier.ApplyAsync(new ProposeTicketSuggestion(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            jobId,
            "TCK-JOB",
            SupportCategory.IT,
            "suggestion",
            [],
            1));
        Assert.True(first.Accepted);

        var replay = await applier.ApplyAsync(new ProposeTicketSuggestion(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            jobId,
            "TCK-JOB",
            SupportCategory.IT,
            "other text",
            [],
            2));
        Assert.True(replay.Accepted);

        var ticket = await scope.ServiceProvider.GetRequiredService<TicketDbContext>().Tickets.FindAsync("TCK-JOB");
        Assert.Equal("suggestion", ticket!.AiSuggestedAnswer);
    }

    [Fact]
    public async Task Duplicate_command_id_returns_same_response()
    {
        var commandId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTicketAsync(provider, "TCK-IDEM", TicketStatus.New, version: 1);

        await using var scope = provider.CreateAsyncScope();
        var applier = scope.ServiceProvider.GetRequiredService<ProposeTicketSuggestionApplier>();
        var request = new ProposeTicketSuggestion(
            commandId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "TCK-IDEM",
            SupportCategory.IT,
            "first",
            [],
            1);

        var first = await applier.ApplyAsync(request);
        var second = await applier.ApplyAsync(request);
        Assert.True(first.Accepted);
        Assert.True(second.Accepted);

        var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
        var commands = await db.ProcessedCommands.Where(x => x.CommandId == commandId).ToListAsync();
        Assert.Single(commands);
    }

    private static ServiceProvider BuildProvider()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new ServiceCollection()
            .AddSingleton(connection)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<TicketDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()))
            .AddScoped<ProposeTicketSuggestionApplier>()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<ProposeTicketSuggestionConsumer>())
            .BuildServiceProvider(true);
    }

    private static async Task SeedTicketAsync(
        ServiceProvider provider,
        string id,
        string status,
        string? finalAnswer = null,
        long version = 1)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Tickets.Add(new TicketEntity
        {
            Id = id,
            EmployeeId = "EMP-1",
            Category = SupportCategory.IT,
            Question = "VPN",
            Status = status,
            FinalAnswer = finalAnswer,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
