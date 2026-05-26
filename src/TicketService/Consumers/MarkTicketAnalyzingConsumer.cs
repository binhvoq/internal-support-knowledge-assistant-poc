using MassTransit;
using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Consumers;

// Consumer xu ly Cmd.MarkTicketAnalyzing tu AiOrchestrator saga.
// MassTransit Inbox tu dong dedupe theo MessageId -> idempotency cap step.
public sealed class MarkTicketAnalyzingConsumer : IConsumer<IMarkTicketAnalyzing>
{
    private readonly TicketDbContext _db;
    private readonly ILogger<MarkTicketAnalyzingConsumer> _logger;

    public MarkTicketAnalyzingConsumer(TicketDbContext db, ILogger<MarkTicketAnalyzingConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<IMarkTicketAnalyzing> context)
    {
        var msg = context.Message;
        var ticket = await _db.Tickets.FindAsync([msg.TicketId], context.CancellationToken);

        if (ticket is null)
        {
            _logger.LogWarning("MarkAnalyzing fail - ticket khong ton tai. TicketId={TicketId}", msg.TicketId);
            await context.Publish<ITicketAnalyzingMarkFailed>(new TicketAnalyzingMarkFailed(
                msg.CorrelationId, msg.TicketId, "Ticket not found"));
            return;
        }

        // FAULT INJECTION (timeout verify): silent return - khong publish event nao.
        // Saga ket o state Analyzing -> Timeout.Received fire sau N giay -> sang Failed.
        // Ticket van duoc cap nhat sang Analyzing de show "MarkAnalyzing da chay xong nhung
        // event bi mat tren broker" - kich ban thuc te khi consumer crash giua chung.
        if (ticket.Question.Has(FaultInjection.ForceSkipMarkAnalyzing))
        {
            ticket.Status = TicketStatus.Analyzing;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(context.CancellationToken);
            _logger.LogWarning("FaultInjection: ForceSkipMarkAnalyzing -> skip publishing event for TicketId={TicketId}", msg.TicketId);
            return;
        }

        // State-based idempotency: neu da Analyzing/Suggested/Resolved roi thi khong PATCH lai.
        if (ticket.Status is TicketStatus.Suggested or TicketStatus.Resolved)
        {
            _logger.LogInformation("Ticket {TicketId} da {Status} - bo qua MarkAnalyzing.", msg.TicketId, ticket.Status);
            await context.Publish<ITicketAnalyzingMarked>(new TicketAnalyzingMarked(msg.CorrelationId, msg.TicketId));
            return;
        }

        if (ticket.Status is not TicketStatus.Analyzing)
        {
            ticket.Status = TicketStatus.Analyzing;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // SaveChangesAsync trong consumer scope se commit cung outbox transaction:
        // - Ticket UPDATE
        // - OutboxMessage INSERT (Publish ITicketAnalyzingMarked)
        // - InboxState INSERT (dedupe MessageId)
        // -> Atomic. Khong con dual-write problem.
        await context.Publish<ITicketAnalyzingMarked>(new TicketAnalyzingMarked(msg.CorrelationId, msg.TicketId));
        await _db.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation("MarkAnalyzing OK TicketId={TicketId} SagaId={SagaId}", msg.TicketId, msg.CorrelationId);
    }
}

public sealed class MarkTicketAnalyzingConsumerDefinition : ConsumerDefinition<MarkTicketAnalyzingConsumer>
{
    public MarkTicketAnalyzingConsumerDefinition()
    {
        EndpointName = "mark-ticket-analyzing";
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<MarkTicketAnalyzingConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000, 2000));
        endpointConfigurator.UseEntityFrameworkOutbox<TicketDbContext>(context);
    }
}
