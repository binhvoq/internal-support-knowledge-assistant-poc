using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportPoc.Shared.Events;

namespace SupportPoc.Shared.Messaging;

public sealed class ServiceBusEventPublisher : ISupportEventPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ServiceBusClient? _client;
    private readonly ServiceBusSender? _sender;
    private readonly ILogger<ServiceBusEventPublisher> _logger;
    private readonly bool _enabled;

    public ServiceBusEventPublisher(IOptions<ServiceBusOptions> options, ILogger<ServiceBusEventPublisher> logger)
    {
        _logger = logger;
        var cfg = options.Value;
        _enabled = cfg.Enabled;
        if (!_enabled)
        {
            _logger.LogWarning("Service Bus chua cau hinh — events se chi log local.");
            return;
        }

        _client = new ServiceBusClient(cfg.ConnectionString);
        _sender = _client.CreateSender(cfg.TopicName);
    }

    public async Task PublishAsync<TPayload>(string eventType, TPayload payload, CancellationToken cancellationToken = default)
    {
        var envelope = new SupportEventEnvelope
        {
            EventType = eventType,
            OccurredAt = DateTimeOffset.UtcNow,
            Payload = payload!
        };

        if (!_enabled || _sender is null)
        {
            _logger.LogInformation("Event {EventType}: {Payload}", eventType, JsonSerializer.Serialize(payload, JsonOptions));
            return;
        }

        var message = new ServiceBusMessage(JsonSerializer.Serialize(envelope, JsonOptions))
        {
            Subject = eventType,
            ContentType = "application/json"
        };
        await _sender.SendMessageAsync(message, cancellationToken);
        _logger.LogInformation("Published {EventType}", eventType);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sender is not null) await _sender.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
    }
}
