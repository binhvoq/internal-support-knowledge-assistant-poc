using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.Shared.Messaging;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Workers;

public sealed class OutboxPublisherWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<OutboxPublisherWorker> _logger;

    public OutboxPublisherWorker(
        IServiceProvider services,
        IOptions<ServiceBusOptions> options,
        ILogger<OutboxPublisherWorker> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox publisher loop loi.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
        var pending = (await db.OutboxMessages
                .Where(x => x.Status == "Pending")
                .ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt)
            .Take(20)
            .ToList();

        if (pending.Count == 0)
            return;

        if (!_options.Enabled)
        {
            foreach (var item in pending)
            {
                item.Status = "Published";
                item.PublishedAt = DateTimeOffset.UtcNow;
                item.Error = "Service Bus disabled; marked published for local PoC.";
                _logger.LogInformation("Outbox local publish {EventType} EventId={EventId}", item.EventType, item.EventId);
            }

            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        await using var client = new ServiceBusClient(_options.ConnectionString);
        var sender = client.CreateSender(_options.TopicName);

        foreach (var item in pending)
        {
            try
            {
                var message = new ServiceBusMessage(item.PayloadJson)
                {
                    MessageId = item.EventId,
                    Subject = item.EventType,
                    ContentType = "application/json"
                };
                await sender.SendMessageAsync(message, cancellationToken);
                item.Status = "Published";
                item.PublishedAt = DateTimeOffset.UtcNow;
                item.Error = null;
                _logger.LogInformation("Outbox published {EventType} EventId={EventId}", item.EventType, item.EventId);
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
                _logger.LogWarning(ex, "Outbox publish that bai {EventType} EventId={EventId}", item.EventType, item.EventId);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
