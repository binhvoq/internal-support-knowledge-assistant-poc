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

public sealed class CompensateWithSagaStopNoteTests
{
    [Fact]
    public async Task Compensate_with_stop_note_reverts_and_persists_note()
    {
        var sagaId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using var provider = new ServiceCollection()
            .AddSingleton(connection)
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
            .AddDbContext<TicketDbContext>((sp, o) => o.UseSqlite(sp.GetRequiredService<SqliteConnection>()))
            .AddMassTransitTestHarness(cfg => cfg.AddConsumer<CompensateMarkAnalyzingConsumer>())
            .BuildServiceProvider(true);

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Tickets.Add(new TicketEntity
            {
                Id = "TCK-NOTE",
                EmployeeId = "EMP-1",
                Category = "IT",
                Question = "VPN",
                Status = TicketStatus.Analyzing,
                RelatedDocumentsJson = "[]",
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
            const string note = "AI suggestion saga stopped; probe unavailable.";
            await harness.Bus.Publish<ICompensateMarkAnalyzing>(
                new CompensateMarkAnalyzing(sagaId, "TCK-NOTE", TicketStatus.New, note));

            Assert.True(await harness.Consumed.Any<ICompensateMarkAnalyzing>());

            await using var verifyScope = provider.CreateAsyncScope();
            var ticket = await verifyScope.ServiceProvider.GetRequiredService<TicketDbContext>()
                .Tickets.FindAsync("TCK-NOTE");
            Assert.NotNull(ticket);
            Assert.Equal(TicketStatus.New, ticket.Status);
            Assert.Null(ticket.ActiveSagaCorrelationId);
            Assert.Equal(note, ticket.SagaStopNote);
            Assert.Equal(3, ticket.SagaEpoch);
        }
        finally
        {
            await harness.Stop();
        }
    }
}
