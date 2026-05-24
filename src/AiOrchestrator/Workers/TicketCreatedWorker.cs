using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Events;
using SupportPoc.Shared.Messaging;

namespace SupportPoc.AiOrchestrator.Workers;

public sealed class TicketCreatedWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceProvider _services;
    private readonly ServiceBusOptions _options;
    private readonly ILogger<TicketCreatedWorker> _logger;
    private ServiceBusProcessor? _processor;

    public TicketCreatedWorker(IServiceProvider services, IOptions<ServiceBusOptions> options, ILogger<TicketCreatedWorker> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogWarning("Service Bus chua cau hinh — TicketCreatedWorker khong chay.");
            return;
        }

        var client = new ServiceBusClient(_options.ConnectionString);
        _processor = client.CreateProcessor(_options.TopicName, "ai-orchestrator", new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false
        });

        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Service Bus loi.");
            return Task.CompletedTask;
        };

        await _processor.StartProcessingAsync(stoppingToken);
        _logger.LogInformation("Dang lang nghe topic {Topic} subscription ai-orchestrator.", _options.TopicName);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SupportEventEnvelope>(args.Message.Body.ToString(), JsonOptions);
            if (envelope?.EventType != SupportEventTypes.TicketCreated)
            {
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            var payload = JsonSerializer.Deserialize<TicketCreatedPayload>(
                JsonSerializer.Serialize(envelope.Payload), JsonOptions);
            if (payload is null)
            {
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            using var scope = _services.CreateScope();
            var suggestionService = scope.ServiceProvider.GetRequiredService<TicketSuggestionService>();
            await suggestionService.ProcessTicketCreatedAsync(payload.TicketId, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Xu ly TicketCreated that bai.");
            await args.AbandonMessageAsync(args.Message);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
            await _processor.StopProcessingAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
