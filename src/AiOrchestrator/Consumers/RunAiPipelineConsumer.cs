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
    private readonly AiPipelineService _pipeline;
    private readonly ILogger<RunAiPipelineConsumer> _logger;

    public RunAiPipelineConsumer(AiPipelineService pipeline, ILogger<RunAiPipelineConsumer> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<IRunAiPipeline> context)
    {
        var msg = context.Message;
        _logger.LogInformation("AI pipeline start TicketId={TicketId} SagaId={SagaId}", msg.TicketId, msg.CorrelationId);

        // FAULT INJECTION (DLQ verify): throw KHONG catch -> MassTransit retry 5 lan -> dead-letter queue.
        // Khac voi ForceAiFail (catch + publish failed event), poison marker test
        // duong di "transport-level error" -> _error queue cua Service Bus.
        if (msg.Question.Has(FaultInjection.ForcePoisonAi))
        {
            _logger.LogError("FaultInjection: ForcePoisonAi marker detected -> throwing uncaught -> retries -> DLQ.");
            throw new InvalidOperationException("Simulated poison message (fault injection) - will be dead-lettered after retries.");
        }

        try
        {
            var result = await _pipeline.RunAsync(msg.Question, msg.Category, context.CancellationToken);
            await context.Publish<IAiPipelineCompleted>(new AiPipelineCompleted(
                msg.CorrelationId,
                msg.TicketId,
                result.Category,
                result.Suggestion,
                result.Related));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI pipeline that bai TicketId={TicketId}", msg.TicketId);
            // Quan trong: KHONG throw - publish failure event de saga compensate.
            // Neu throw, MassTransit retry consumer -> co the chay LLM nhieu lan (ton tien).
            // Throw chi nen dung cho transient infra error.
            await context.Publish<IAiPipelineFailed>(new AiPipelineFailed(
                msg.CorrelationId,
                msg.TicketId,
                ex.Message));
        }
    }
}

// Consumer definition - cau hinh retry/endpoint cho RunAiPipeline.
public sealed class RunAiPipelineConsumerDefinition : ConsumerDefinition<RunAiPipelineConsumer>
{
    public RunAiPipelineConsumerDefinition()
    {
        EndpointName = "run-ai-pipeline";
        // AI ton tien -> retry it.
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<RunAiPipelineConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        // Retry intervals khop voi MaxDeliveryCount = 5 (transport-level redelivery)
        // 4 retry o app-layer + 1 lan dau = 5. Sau khi het, ASB tu chuyen sang DLQ.
        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 2000, 5000, 10000));

        // ---------------------------------------------------------------------
        // LOCK DURATION FIX (AI pipeline co the chay 30s+ khi LLM cold-start).
        // - LockDuration = 5 phut (gia tri max ma Azure Service Bus cho phep).
        //   QUAN TRONG: setting nay chi co tac dung khi queue chua ton tai
        //   (luc MassTransit auto-create). Neu queue da co, can xoa de re-create
        //   hoac chinh tay trong Azure portal. -> xem doc/POC-RESILIENCE.md.
        // - MaxAutoRenewDuration = 15 phut: MassTransit auto-renew lock runtime,
        //   khong can recreate queue. Day la fix "an toan" cho dev env.
        // - MaxDeliveryCount = 5: sau 5 lan delivery fail, ASB tu chuyen DLQ.
        // ---------------------------------------------------------------------
        if (endpointConfigurator is IServiceBusReceiveEndpointConfigurator sb)
        {
            sb.LockDuration = TimeSpan.FromMinutes(5);
            sb.MaxAutoRenewDuration = TimeSpan.FromMinutes(15);
            sb.MaxDeliveryCount = 5;

            // Default MassTransit chuyen message faulted vao queue phu '_error'.
            // Bat cau hinh nay de chuyen sang ASB native Dead-Letter Queue ($DeadLetterQueue
            // cua chinh queue run-ai-pipeline). Loi the:
            //   - Inspect bang Azure Portal / Service Bus Explorer tu nhien.
            //   - Co the resubmit qua tooling chuan.
            //   - Don gian cho operator: 1 cho de tim message poison.
            sb.ConfigureDeadLetterQueueErrorTransport();
            sb.ConfigureDeadLetterQueueDeadLetterTransport();
        }
    }
}
