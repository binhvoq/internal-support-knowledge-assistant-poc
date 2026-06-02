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

public sealed class SaveTicketSuggestionStaleEpochTests
{
    [Fact]
    public async Task Stale_save_after_agent_resolve_does_not_overwrite_resolved_status()
    {
        var sagaId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var provider = new ServiceCollection()
            .AddSingleton(connection)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<TicketDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()))
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<SaveTicketSuggestionConsumer>())
            .BuildServiceProvider(true);

        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tickets.Add(new TicketEntity
            {
                Id = "TCK-STALE",
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

        await harness.Bus.Publish<ISaveTicketSuggestion>(new SaveTicketSuggestion(
            sagaId,
            "TCK-STALE",
            ExpectedEpoch: 1,
            SupportCategory.IT,
            "Late saga suggestion",
            []));

        Assert.True(await harness.Consumed.Any<ISaveTicketSuggestion>());

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            var ticket = await db.Tickets.FindAsync("TCK-STALE");
            Assert.NotNull(ticket);
            Assert.Equal(TicketStatus.Resolved, ticket!.Status);
            Assert.Equal("Agent resolved first", ticket.FinalAnswer);
            Assert.Null(ticket.AiSuggestedAnswer);
        }

        Assert.False(await harness.Published.Any<ITicketSuggestionSaved>());
    }
}
