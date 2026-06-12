using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Consumers;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Tests;

/// <summary>
/// Transport inbox dedup (MassTransit EF consumer outbox) = same <see cref="ConsumeContext.MessageId"/>.
/// Business idempotency = <c>ProcessedCommands.CommandId</c> (separate layer).
/// </summary>
public sealed class ProposeTicketSuggestionTransportInboxTests : IAsyncLifetime
{
    private readonly string _connectionString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keeperConnection;
    private ServiceProvider _provider = null!;
    private IBusControl _bus = null!;

    public ProposeTicketSuggestionTransportInboxTests()
    {
        _keeperConnection = new SqliteConnection(_connectionString);
    }

    public async Task InitializeAsync()
    {
        await _keeperConnection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<TicketDbContext>(o => o.UseSqlite(_connectionString));
        services.AddScoped<ProposeTicketSuggestionApplier>();

        services.AddMassTransit(mt =>
        {
            mt.AddConsumer<ProposeTicketSuggestionConsumer, TestProposeTicketSuggestionConsumerDefinition>();
            mt.AddEntityFrameworkOutbox<TicketDbContext>(o =>
            {
                o.UseSqlite();
                o.DuplicateDetectionWindow = TimeSpan.FromHours(1);
            });
            mt.AddEntityFrameworkConsumerOutbox<TicketDbContext>();
            mt.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
        });

        _provider = services.BuildServiceProvider(true);
        _bus = _provider.GetRequiredService<IBusControl>();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            await db.Database.EnsureCreatedAsync();
            await SeedTicketAsync(db);
        }

        await _bus.StartAsync();
    }

    [Fact]
    public async Task Duplicate_MessageId_redelivery_does_not_apply_ticket_twice()
    {
        var messageId = Guid.NewGuid();
        var message = CreateMessage();
        var consumerAddress = new Uri("loopback://localhost/propose-ticket-suggestion");
        var sendEndpoint = await _bus.GetSendEndpoint(consumerAddress);

        await sendEndpoint.Send(message, ctx => ctx.MessageId = messageId);
        await WaitUntilAsync(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            return await db.ProcessedCommands.AnyAsync();
        });

        await sendEndpoint.Send(message, ctx => ctx.MessageId = messageId);
        await Task.Delay(300);

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            db.ChangeTracker.Clear();

            Assert.Equal(1, await db.ProcessedCommands.CountAsync());
            var ticket = await db.Tickets.AsNoTracking().SingleAsync();
            Assert.Equal(TicketStatus.Suggested, ticket.Status);
            Assert.Equal(2, ticket.Version);

            var inbox = await db.Set<InboxState>().AsNoTracking().ToListAsync();
            var inboxRow = Assert.Single(inbox);
            Assert.Equal(messageId, inboxRow.MessageId);
            Assert.True(inboxRow.Consumed.HasValue);
        }
    }

    private async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await predicate())
            await Task.Delay(50, cts.Token);
    }

    private static ProposeTicketSuggestion CreateMessage() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "TCK-INBOX",
            SupportCategory.IT,
            "suggested answer",
            [],
            1);

    private static async Task SeedTicketAsync(TicketDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Tickets.Add(new TicketEntity
        {
            Id = "TCK-INBOX",
            EmployeeId = "emp@test",
            Category = SupportCategory.Other,
            Question = "question?",
            Status = TicketStatus.New,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_bus is not null)
            await _bus.StopAsync();
        if (_provider is not null)
            await _provider.DisposeAsync();
        await _keeperConnection.DisposeAsync();
    }
}
