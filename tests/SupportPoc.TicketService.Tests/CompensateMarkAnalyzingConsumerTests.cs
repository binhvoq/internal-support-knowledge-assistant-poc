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

namespace SupportPoc.TicketService.Tests;

public sealed class CompensateMarkAnalyzingConsumerTests
{
    [Fact]
    public async Task Already_reverted_ticket_publishes_reverted_without_mutating()
    {
        var otherSagaId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var provider = new ServiceCollection()
            .AddSingleton(connection)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<TicketDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()))
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<CompensateMarkAnalyzingConsumer>())
            .BuildServiceProvider(true);

        var updatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tickets.Add(new TicketEntity
            {
                Id = "TCK-IDEM",
                EmployeeId = "EMP-1",
                Category = string.Empty,
                Question = "VPN issue",
                Status = TicketStatus.New,
                AiSuggestedAnswer = null,
                RelatedDocumentsJson = "[]",
                SagaEpoch = 3,
                ActiveSagaCorrelationId = null,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            });
            await db.SaveChangesAsync();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish<ICompensateMarkAnalyzing>(
                new CompensateMarkAnalyzing(otherSagaId, "TCK-IDEM", TicketStatus.New));

            Assert.True(await harness.Consumed.Any<ICompensateMarkAnalyzing>());
            Assert.True(await harness.Published.Any<IMarkAnalyzingReverted>());

            var published = harness.Published.Select<IMarkAnalyzingReverted>().First().Context.Message;
            Assert.Equal(otherSagaId, published.CorrelationId);
            Assert.Equal("TCK-IDEM", published.TicketId);

            await using var verifyScope = provider.CreateAsyncScope();
            var ticket = await verifyScope.ServiceProvider.GetRequiredService<TicketDbContext>()
                .Tickets.FindAsync("TCK-IDEM");
            Assert.NotNull(ticket);
            Assert.Equal(TicketStatus.New, ticket.Status);
            Assert.Null(ticket.ActiveSagaCorrelationId);
            Assert.Null(ticket.AiSuggestedAnswer);
            Assert.Equal(string.Empty, ticket.Category);
            Assert.Equal(3, ticket.SagaEpoch);
            Assert.Equal(updatedAt, ticket.UpdatedAt);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Superseded_resolved_ticket_publishes_reverted_without_mutating()
    {
        var sagaId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var provider = new ServiceCollection()
            .AddSingleton(connection)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<TicketDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()))
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<CompensateMarkAnalyzingConsumer>())
            .BuildServiceProvider(true);

        var updatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tickets.Add(new TicketEntity
            {
                Id = "TCK-SUPER",
                EmployeeId = "EMP-1",
                Category = SupportCategory.IT,
                Question = "VPN",
                Status = TicketStatus.Resolved,
                FinalAnswer = "Agent resolved",
                SagaEpoch = 3,
                ActiveSagaCorrelationId = null,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            });
            await db.SaveChangesAsync();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish<ICompensateMarkAnalyzing>(
                new CompensateMarkAnalyzing(sagaId, "TCK-SUPER", TicketStatus.New));

            Assert.True(await harness.Published.Any<IMarkAnalyzingReverted>(x =>
                x.Context.Message.CorrelationId == sagaId));

            await using var verifyScope = provider.CreateAsyncScope();
            var ticket = await verifyScope.ServiceProvider.GetRequiredService<TicketDbContext>()
                .Tickets.FindAsync("TCK-SUPER");
            Assert.NotNull(ticket);
            Assert.Equal(TicketStatus.Resolved, ticket!.Status);
            Assert.Equal("Agent resolved", ticket.FinalAnswer);
            Assert.Equal(3, ticket.SagaEpoch);
            Assert.Equal(updatedAt, ticket.UpdatedAt);
        }
        finally
        {
            await harness.Stop();
        }
    }
}
