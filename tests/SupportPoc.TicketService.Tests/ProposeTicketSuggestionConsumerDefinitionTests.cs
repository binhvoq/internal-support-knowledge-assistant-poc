using MassTransit;
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

public sealed class ProposeTicketSuggestionConsumerDefinitionTests : IAsyncLifetime
{
    /// <summary>Phai trung queue name trong <see cref="ProposeTicketSuggestionConsumerDefinition"/>.</summary>
    private const string ExpectedEndpointName = "propose-ticket-suggestion";

    private readonly string _connectionString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keeperConnection;
    private ServiceProvider _provider = null!;
    private IBusControl _bus = null!;

    public ProposeTicketSuggestionConsumerDefinitionTests()
    {
        _keeperConnection = new SqliteConnection(_connectionString);
    }

    /// <summary>
    /// Smoke: production consumer definition dang ky va consume duoc tren in-memory bus tai queue
    /// <c>{ExpectedEndpointName}</c> (trung voi <see cref="TestProposeTicketSuggestionConsumerDefinition"/>).
    /// DLQ/retry ASB chi ap dung khi endpoint la IServiceBusReceiveEndpointConfigurator — khong assert truc tiep o day.
    /// </summary>
    [Fact]
    public async Task Production_definition_binds_consumer_on_in_memory_bus()
    {
        var messageId = Guid.NewGuid();
        var message = CreateMessage();
        var consumerAddress = new Uri($"loopback://localhost/{ExpectedEndpointName}");
        var sendEndpoint = await _bus.GetSendEndpoint(consumerAddress);

        await sendEndpoint.Send(message, ctx => ctx.MessageId = messageId);
        await WaitUntilAsync(async () =>
        {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            return await db.ProcessedCommands.AnyAsync();
        });

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
            var ticket = await db.Tickets.AsNoTracking().SingleAsync();
            Assert.Equal(TicketStatus.Suggested, ticket.Status);
        }
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
            mt.AddConsumer<ProposeTicketSuggestionConsumer, ProposeTicketSuggestionConsumerDefinition>();
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
            TestTicketIds.Inbox,
            SupportCategory.IT,
            "suggested answer",
            [],
            1);

    private static async Task SeedTicketAsync(TicketDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Tickets.Add(new TicketEntity
        {
            Id = TestTicketIds.Inbox,
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
