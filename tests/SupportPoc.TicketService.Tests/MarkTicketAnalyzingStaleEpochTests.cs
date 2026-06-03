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

public sealed class MarkTicketAnalyzingStaleEpochTests
{
    [Fact]
    public async Task Stale_mark_after_agent_resolve_publishes_mark_failed()
    {
        var sagaId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var provider = new ServiceCollection()
            .AddSingleton(connection)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<TicketDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()))
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<MarkTicketAnalyzingConsumer>())
            .BuildServiceProvider(true);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tickets.Add(new TicketEntity
            {
                Id = "TCK-MARK-STALE",
                EmployeeId = "EMP-1",
                Category = SupportCategory.IT,
                Question = "VPN",
                Status = TicketStatus.Resolved,
                FinalAnswer = "Agent resolved first",
                SagaEpoch = 2,
                ActiveSagaCorrelationId = null,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        await harness.Bus.Publish<IMarkTicketAnalyzing>(new MarkTicketAnalyzing(
            sagaId,
            "TCK-MARK-STALE",
            ExpectedEpoch: 1));

        Assert.True(await harness.Consumed.Any<IMarkTicketAnalyzing>());
        Assert.False(await harness.Published.Any<ITicketAnalyzingMarked>());
        Assert.True(await harness.Published.Any<ITicketAnalyzingMarkFailed>(x =>
            x.Context.Message.CorrelationId == sagaId &&
            x.Context.Message.Reason.Contains("Stale command", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Mark_already_applied_with_lost_event_publishes_marked_idempotently()
    {
        var sagaId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var provider = new ServiceCollection()
            .AddSingleton(connection)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<TicketDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()))
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<MarkTicketAnalyzingConsumer>())
            .BuildServiceProvider(true);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tickets.Add(new TicketEntity
            {
                Id = "TCK-MARK-IDEM",
                EmployeeId = "EMP-1",
                Category = SupportCategory.IT,
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

        await harness.Bus.Publish<IMarkTicketAnalyzing>(new MarkTicketAnalyzing(
            sagaId,
            "TCK-MARK-IDEM",
            ExpectedEpoch: 1));

        Assert.True(await harness.Published.Any<ITicketAnalyzingMarked>(x =>
            x.Context.Message.CorrelationId == sagaId &&
            x.Context.Message.SagaEpoch == 2));
        Assert.False(await harness.Published.Any<ITicketAnalyzingMarkFailed>());
    }
}
