using MassTransit;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Consumers;

// Compensating transaction: revert ticket ve OriginalStatus (truoc khi MarkAnalyzing).
// QUAN TRONG: compensation phai idempotent - co the chay nhieu lan an toan.
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
            // Khong co gi de compensate -> coi nhu success (semantic rollback voi state khong ton tai = no-op).
            _logger.LogWarning("Compensate: ticket khong ton tai. TicketId={TicketId}", msg.TicketId);
            await context.Publish<IMarkAnalyzingReverted>(new MarkAnalyzingReverted(msg.CorrelationId, msg.TicketId));
            return;
        }

        // Chi revert khi ticket dang Analyzing va chua co AI suggestion.
        // Neu da Suggested/Resolved -> KHONG revert (semantic correctness:
        // saga compensate khong duoc xoa cong viec da hoan thanh).
        if (ticket.Status == TicketStatus.Analyzing && string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer))
        {
            ticket.Status = string.IsNullOrWhiteSpace(msg.OriginalStatus) ? TicketStatus.New : msg.OriginalStatus;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Compensated TicketId={TicketId} back to {Status}", msg.TicketId, ticket.Status);
        }
        else
        {
            _logger.LogInformation("Compensate skip - ticket {TicketId} state={Status} (no rollback needed).", msg.TicketId, ticket.Status);
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
        // Compensation cuc ki quan trong - retry aggressive hon.
        endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000, 2000, 5000, 10000));
        endpointConfigurator.UseDelayedRedelivery(r => r.Intervals(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        endpointConfigurator.UseEntityFrameworkOutbox<TicketDbContext>(context);
    }
}
