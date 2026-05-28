using MassTransit;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

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

        var ownsTicket = ticket.ActiveSagaCorrelationId == msg.CorrelationId;
        if (ownsTicket)
        {
            var oldStatus = ticket.Status;
            var targetStatus = string.IsNullOrWhiteSpace(msg.OriginalStatus) ? TicketStatus.New : msg.OriginalStatus;
            ticket.Status = targetStatus;
            ticket.Category = string.Empty;
            ticket.AiSuggestedAnswer = null;
            ticket.RelatedDocumentsJson = "[]";
            ticket.ActiveSagaCorrelationId = null;
            ticket.SagaEpoch++;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Compensated TicketId={TicketId}: status={OldStatus} -> {NewStatus}, epoch={Epoch} (AI cleared)",
                msg.TicketId,
                oldStatus,
                targetStatus,
                ticket.SagaEpoch);
        }
        else
        {
            _logger.LogWarning(
                "Compensate skip DB mutate TicketId={TicketId} activeSaga={Active} msgSaga={MsgSaga}",
                msg.TicketId,
                ticket.ActiveSagaCorrelationId,
                msg.CorrelationId);
        }

        await context.Publish<IMarkAnalyzingReverted>(new MarkAnalyzingReverted(msg.CorrelationId, msg.TicketId));
        await _db.SaveChangesAsync(context.CancellationToken);
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
