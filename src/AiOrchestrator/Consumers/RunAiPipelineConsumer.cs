using MassTransit;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Testing;

namespace SupportPoc.AiOrchestrator.Consumers;

// Consumer xu ly Cmd.RunAiPipeline (internal command tu saga).
// Tach rieng khoi state machine vi 2 ly do:
// 1) AI pipeline co the chay lau (LLM 30s+) - khong nen lock saga instance.
// 2) Co the apply retry policy + DLQ rieng cho buoc AI (vd. quota error -> dung lai).
public sealed class RunAiPipelineConsumer : IConsumer<IRunAiPipeline>
{
    private static readonly TimeSpan DraftRecordRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly Uri RecordDraftAddress = new("queue:record-ai-pipeline-draft");

    private readonly AiPipelineService _pipeline;
    private readonly IBus _bus;
    private readonly ILogger<RunAiPipelineConsumer> _logger;

    public RunAiPipelineConsumer(
        AiPipelineService pipeline,
        IBus bus,
        ILogger<RunAiPipelineConsumer> logger)
    {
        _pipeline = pipeline;
        _bus = bus;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<IRunAiPipeline> context)
    {
        var msg = context.Message;
        _logger.LogInformation("AI pipeline start TicketId={TicketId} SagaId={SagaId}", msg.TicketId, msg.CorrelationId);

        // FAULT INJECTION (DLQ verify): throw KHONG catch -> MassTransit retry 5 lan -> dead-letter queue.
        if (msg.Question.Has(FaultInjection.ForcePoisonAi))
        {
            _logger.LogError("FaultInjection: ForcePoisonAi marker detected -> throwing uncaught -> retries -> DLQ.");
            throw new InvalidOperationException("Simulated poison message (fault injection) - will be dead-lettered after retries.");
        }

        try
        {
            var result = await _pipeline.RunAsync(msg.Question, msg.Category, context.CancellationToken);

            var draftClient = _bus.CreateRequestClient<IRecordAiPipelineDraft>(RecordDraftAddress, DraftRecordRequestTimeout);
            var draftResponse = await draftClient.GetResponse<IAiPipelineDraftRecorded, IAiPipelineDraftRejected>(
                new RecordAiPipelineDraft(
                    msg.CorrelationId,
                    msg.TicketId,
                    msg.ExpectedEpoch,
                    result.Category,
                    result.Suggestion,
                    result.Related),
                context.CancellationToken);

            if (draftResponse.Is(out Response<IAiPipelineDraftRejected>? rejected))
            {
                _logger.LogError(
                    "RecordAiPipelineDraft rejected TicketId={TicketId} SagaId={SagaId} Reason={Reason}",
                    msg.TicketId,
                    msg.CorrelationId,
                    rejected!.Message.Reason);
                await context.Publish<IAiPipelineFailed>(new AiPipelineFailed(
                    msg.CorrelationId,
                    msg.TicketId,
                    rejected.Message.Reason));
                return;
            }

            var skipCompletedEvent = msg.Question.Has(FaultInjection.ForceSkipAiPipelineCompletedEvent);
            if (!skipCompletedEvent)
            {
                await context.Publish<IAiPipelineCompleted>(new AiPipelineCompleted(
                    msg.CorrelationId,
                    msg.TicketId,
                    result.Category,
                    result.Suggestion,
                    result.Related));
            }
            else
            {
                _logger.LogWarning(
                    "FaultInjection: ForceSkipAiPipelineCompletedEvent -> draft saved but skipped AiPipelineCompleted. TicketId={TicketId}",
                    msg.TicketId);
            }
        }
        catch (RequestTimeoutException ex)
        {
            _logger.LogError(ex, "RecordAiPipelineDraft request timeout TicketId={TicketId}", msg.TicketId);
            await context.Publish<IAiPipelineFailed>(new AiPipelineFailed(
                msg.CorrelationId,
                msg.TicketId,
                "Timed out recording AI draft at TicketService"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI pipeline that bai TicketId={TicketId}", msg.TicketId);
            await context.Publish<IAiPipelineFailed>(new AiPipelineFailed(
                msg.CorrelationId,
                msg.TicketId,
                ex.Message));
        }
    }
}

public sealed class RunAiPipelineConsumerDefinition : ConsumerDefinition<RunAiPipelineConsumer>
{
    public RunAiPipelineConsumerDefinition()
    {
        EndpointName = "run-ai-pipeline";
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<RunAiPipelineConsumer> consumerConfigurator,
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
