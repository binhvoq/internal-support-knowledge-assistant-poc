using MassTransit;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Testing;

namespace SupportPoc.AiOrchestrator.Consumers;

/// <summary>AI worker — chi chay pipeline va reply ve saga, khong goi TicketService.</summary>
public sealed class GenerateSuggestionRequestedConsumer : IConsumer<IGenerateSuggestionRequested>
{
    private readonly AiPipelineService _pipeline;
    private readonly ILogger<GenerateSuggestionRequestedConsumer> _logger;

    public GenerateSuggestionRequestedConsumer(
        AiPipelineService pipeline,
        ILogger<GenerateSuggestionRequestedConsumer> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<IGenerateSuggestionRequested> context)
    {
        var msg = context.Message;

        if (msg.Question.Has(FaultInjection.ForcePoisonAi))
        {
            _logger.LogError("FaultInjection: ForcePoisonAi -> poison for DLQ test.");
            throw new InvalidOperationException("Simulated poison message (fault injection).");
        }

        if (msg.Question.Has(FaultInjection.ForceSkipConsider) || msg.Question.Has(FaultInjection.ForceSkipGenerate))
        {
            _logger.LogWarning(
                "FaultInjection: skip generate SagaId={SagaId} AttemptId={AttemptId}",
                msg.SagaId,
                msg.AttemptId);
            return;
        }

        try
        {
            var result = await _pipeline.RunAsync(msg.Question, msg.Category, context.CancellationToken);
            await context.Publish<ISuggestionGenerated>(new SuggestionGenerated(
                msg.SagaId,
                msg.AttemptId,
                msg.JobId,
                msg.TicketId,
                result.Category,
                result.Suggestion,
                result.Related));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Suggestion generation failed SagaId={SagaId} AttemptId={AttemptId}",
                msg.SagaId,
                msg.AttemptId);
            await context.Publish<ISuggestionGenerationFailed>(new SuggestionGenerationFailed(
                msg.SagaId,
                msg.AttemptId,
                msg.JobId,
                msg.TicketId,
                ex.Message));
        }
    }
}

public sealed class GenerateSuggestionRequestedConsumerDefinition
    : ConsumerDefinition<GenerateSuggestionRequestedConsumer>
{
    public GenerateSuggestionRequestedConsumerDefinition()
    {
        EndpointName = "generate-suggestion-requested";
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<GenerateSuggestionRequestedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 2000, 5000, 10000));

        if (endpointConfigurator is IServiceBusReceiveEndpointConfigurator sb)
        {
            sb.LockDuration = TimeSpan.FromMinutes(5);
            sb.MaxAutoRenewDuration = TimeSpan.FromMinutes(15);
            sb.MaxDeliveryCount = 5;
            sb.ConfigureDeadLetterQueueErrorTransport();
            sb.ConfigureDeadLetterQueueDeadLetterTransport();
        }
    }
}
