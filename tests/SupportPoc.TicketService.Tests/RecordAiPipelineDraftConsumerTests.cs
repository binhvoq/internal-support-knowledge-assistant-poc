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

public sealed class RecordAiPipelineDraftConsumerTests
{
    [Fact]
    public async Task Records_draft_and_responds_recorded()
    {
        var sagaId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tickets.Add(new TicketEntity
            {
                Id = "TCK-DRAFT",
                EmployeeId = "E1",
                Category = "IT",
                Question = "VPN",
                Status = TicketStatus.Analyzing,
                SagaEpoch = 1,
                ActiveSagaCorrelationId = sagaId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var client = harness.GetRequestClient<IRecordAiPipelineDraft>();
            var response = await client.GetResponse<IAiPipelineDraftRecorded>(
                new RecordAiPipelineDraft(
                    sagaId,
                    "TCK-DRAFT",
                    1,
                    "IT",
                    "Use portal reset",
                    Array.Empty<RelatedDocument>()));

            Assert.NotNull(response.Message);

            await using var verifyScope = provider.CreateAsyncScope();
            var ticket = await verifyScope.ServiceProvider.GetRequiredService<TicketDbContext>()
                .Tickets.FindAsync("TCK-DRAFT");
            Assert.Equal("Use portal reset", ticket!.AiDraftSuggestion);
            Assert.Equal(sagaId, ticket.AiDraftCorrelationId);
            Assert.Equal(1, ticket.AiDraftSagaEpoch);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task Stale_epoch_responds_rejected()
    {
        var sagaId = Guid.NewGuid();
        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tickets.Add(new TicketEntity
            {
                Id = "TCK-STALE",
                EmployeeId = "E1",
                Category = "IT",
                Question = "VPN",
                Status = TicketStatus.Analyzing,
                SagaEpoch = 2,
                ActiveSagaCorrelationId = sagaId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var client = harness.GetRequestClient<IRecordAiPipelineDraft>();
            var response = await client.GetResponse<IAiPipelineDraftRejected>(
                new RecordAiPipelineDraft(
                    sagaId,
                    "TCK-STALE",
                    1,
                    "IT",
                    "ignored",
                    Array.Empty<RelatedDocument>()));

            Assert.Contains("Stale", response.Message.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static ServiceProvider BuildProvider()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        return new ServiceCollection()
            .AddSingleton(connection)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<TicketDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()))
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<RecordAiPipelineDraftConsumer>())
            .BuildServiceProvider(true);
    }
}
