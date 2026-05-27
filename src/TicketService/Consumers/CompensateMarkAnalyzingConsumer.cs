using MassTransit;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Consumers;

// Compensating transaction: revert ticket ve OriginalStatus (truoc khi MarkAnalyzing).
// QUAN TRONG: compensation phai idempotent - co the chay nhieu lan an toan.
// Nghiep vu moi: neu saga fail/timeout thi PHAI rollback 100% ve state ban dau,
// ke ca truong hop da save AI suggestion (khong con "semantic skip").
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
            // Ticket khong ton tai -> coi nhu da rollback xong (idempotent).
            _logger.LogWarning("Compensate: ticket khong ton tai. TicketId={TicketId}", msg.TicketId);
            await context.Publish<IMarkAnalyzingReverted>(new MarkAnalyzingReverted(msg.CorrelationId, msg.TicketId));
            return;
        }

        // Rollback 100% ve state ban dau:
        // - Status ve OriginalStatus (default New neu OriginalStatus rong)
        // - Xoa du lieu AI (neu da save) de tranh "compensated nhung ticket van Suggested"
        var oldStatus = ticket.Status;
        var targetStatus = string.IsNullOrWhiteSpace(msg.OriginalStatus) ? TicketStatus.New : msg.OriginalStatus;
        ticket.Status = targetStatus;
        ticket.Category = string.Empty;
        ticket.AiSuggestedAnswer = null;
        ticket.RelatedDocumentsJson = "[]";
        ticket.UpdatedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Compensated TicketId={TicketId}: status={OldStatus} -> {NewStatus} (AI fields cleared)",
            msg.TicketId,
            oldStatus,
            targetStatus);

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
        // Compensation cuc ki quan trong - retry aggressive hon.
        endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000, 2000, 5000, 10000));
        endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        endpointConfigurator.UseEntityFrameworkOutbox<TicketDbContext>(context);
    }
}
