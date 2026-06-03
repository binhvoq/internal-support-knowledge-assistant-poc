using MassTransit;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Consumers;

// Compensating transaction: revert ticket ve OriginalStatus (truoc khi MarkAnalyzing).
// SagaEpoch++ trong cung transaction de vo hieu lenh Save/Mark tre.
public sealed class CompensateMarkAnalyzingConsumer : IConsumer<ICompensateMarkAnalyzing>
{
    private readonly TicketDbContext _db;
    private readonly ILogger<CompensateMarkAnalyzingConsumer> _logger;

    public CompensateMarkAnalyzingConsumer(TicketDbContext db, ILogger<CompensateMarkAnalyzingConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ICompensateMarkAnalyzing> context)
    {
        var msg = context.Message;
        var ticket = await _db.Tickets.FindAsync([msg.TicketId], context.CancellationToken);

        if (ticket is null)
        {
            _logger.LogWarning("Compensate: ticket khong ton tai. TicketId={TicketId}", msg.TicketId);
            await context.Publish<IMarkAnalyzingReverted>(new MarkAnalyzingReverted(msg.CorrelationId, msg.TicketId));
            return;
        }

        if (IsAlreadyReverted(ticket, msg))
        {
            if (!string.IsNullOrWhiteSpace(msg.SagaStopNote))
            {
                ticket.SagaStopNote = msg.SagaStopNote;
                ticket.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(context.CancellationToken);
            }

            _logger.LogInformation(
                "Compensate idempotent: ticket already reverted. TicketId={TicketId} SagaId={SagaId} Status={Status}",
                msg.TicketId,
                msg.CorrelationId,
                ticket.Status);
            await context.Publish<IMarkAnalyzingReverted>(new MarkAnalyzingReverted(msg.CorrelationId, msg.TicketId));
            return;
        }

        var skipRevertEvent = ticket.Question.Has(FaultInjection.ForceSkipCompensateRevertedEvent);
        var ownsTicket = ticket.ActiveSagaCorrelationId == msg.CorrelationId;
        if (ownsTicket)
        {
            var oldStatus = ticket.Status;
            var targetStatus = string.IsNullOrWhiteSpace(msg.OriginalStatus) ? TicketStatus.New : msg.OriginalStatus;
            ticket.Status = targetStatus;
            ticket.Category = string.Empty;
            ticket.AiSuggestedAnswer = null;
            ticket.RelatedDocumentsJson = "[]";
            TicketAiDraftHelper.ClearDraft(ticket);
            ticket.ActiveSagaCorrelationId = null;
            ticket.SagaEpoch++;
            if (!string.IsNullOrWhiteSpace(msg.SagaStopNote))
                ticket.SagaStopNote = msg.SagaStopNote;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Compensated TicketId={TicketId}: status={OldStatus} -> {NewStatus}, epoch={Epoch} (AI cleared)",
                msg.TicketId,
                oldStatus,
                targetStatus,
                ticket.SagaEpoch);

            if (!skipRevertEvent)
                await context.Publish<IMarkAnalyzingReverted>(new MarkAnalyzingReverted(msg.CorrelationId, msg.TicketId));

            await _db.SaveChangesAsync(context.CancellationToken);

            if (skipRevertEvent)
            {
                _logger.LogWarning(
                    "FaultInjection: ForceSkipCompensateRevertedEvent -> reverted DB but skipped MarkAnalyzingReverted publish. TicketId={TicketId}",
                    msg.TicketId);
            }

            return;
        }

        if (SagaCommandFeedback.IsSupersededForCompensate(ticket, msg))
        {
            _logger.LogInformation(
                "Compensate noop: ticket superseded by agent or another saga. TicketId={TicketId} Status={Status} activeSaga={Active}",
                msg.TicketId,
                ticket.Status,
                ticket.ActiveSagaCorrelationId);
            await context.Publish<IMarkAnalyzingReverted>(new MarkAnalyzingReverted(msg.CorrelationId, msg.TicketId));
            return;
        }

        _logger.LogWarning(
            "Compensate skip: ticket not owned and not in reverted shape — no MarkAnalyzingReverted. TicketId={TicketId} activeSaga={Active} msgSaga={MsgSaga} Status={Status}",
            msg.TicketId,
            ticket.ActiveSagaCorrelationId,
            msg.CorrelationId,
            ticket.Status);
    }

    private static bool IsAlreadyReverted(TicketEntity ticket, ICompensateMarkAnalyzing msg)
    {
        var targetStatus = string.IsNullOrWhiteSpace(msg.OriginalStatus) ? TicketStatus.New : msg.OriginalStatus;
        if (ticket.Status != targetStatus)
            return false;

        if (!string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer))
            return false;

        return ticket.ActiveSagaCorrelationId != msg.CorrelationId;
    }
}

public sealed class CompensateMarkAnalyzingConsumerDefinition : ConsumerDefinition<CompensateMarkAnalyzingConsumer>
{
    public CompensateMarkAnalyzingConsumerDefinition()
    {
        EndpointName = "compensate-mark-analyzing";
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<CompensateMarkAnalyzingConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000, 2000, 5000, 10000));
        endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        endpointConfigurator.UseEntityFrameworkOutbox<TicketDbContext>(context);
    }
}
