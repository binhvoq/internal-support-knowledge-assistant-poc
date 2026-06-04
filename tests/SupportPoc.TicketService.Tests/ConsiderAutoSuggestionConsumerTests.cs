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

public sealed class ConsiderAutoSuggestionConsumerTests
{
    [Fact]
    public async Task Accepts_proposal_when_ticket_is_new()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTicketAsync(provider, "TCK-OK", TicketStatus.New);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var client = harness.GetRequestClient<IConsiderAutoSuggestion>();
        var response = await client.GetResponse<IAutoSuggestionAccepted>(
            new ConsiderAutoSuggestion(jobId, "TCK-OK", SupportCategory.IT, "suggestion", []));

        Assert.NotNull(response.Message);

        await using var scope = provider.CreateAsyncScope();
        var ticket = await scope.ServiceProvider.GetRequiredService<TicketDbContext>().Tickets.FindAsync("TCK-OK");
        Assert.Equal(TicketStatus.Suggested, ticket!.Status);
        Assert.Equal("suggestion", ticket.AiSuggestedAnswer);
    }

    [Fact]
    public async Task Rejects_when_agent_already_resolved()
    {
        var jobId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await SeedTicketAsync(provider, "TCK-RES", TicketStatus.Resolved, finalAnswer: "agent");

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var client = harness.GetRequestClient<IConsiderAutoSuggestion>();
        var response = await client.GetResponse<IAutoSuggestionRejected>(
            new ConsiderAutoSuggestion(jobId, "TCK-RES", SupportCategory.IT, "late", []));

        Assert.Contains("final answer", response.Message.Reason, StringComparison.OrdinalIgnoreCase);

        await using var scope = provider.CreateAsyncScope();
        var ticket = await scope.ServiceProvider.GetRequiredService<TicketDbContext>().Tickets.FindAsync("TCK-RES");
        Assert.Equal(TicketStatus.Resolved, ticket!.Status);
        Assert.Null(ticket.AiSuggestedAnswer);
    }

    private static ServiceProvider BuildProvider()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new ServiceCollection()
            .AddSingleton(connection)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<TicketDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()))
            .AddScoped<ConsiderAutoSuggestionApplier>()
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<ConsiderAutoSuggestionConsumer>())
            .BuildServiceProvider(true);
    }

    private static async Task SeedTicketAsync(
        ServiceProvider provider,
        string id,
        string status,
        string? finalAnswer = null)
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
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
