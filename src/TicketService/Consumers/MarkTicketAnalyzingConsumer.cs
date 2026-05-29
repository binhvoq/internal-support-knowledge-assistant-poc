using MassTransit;
using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

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

        if (ticket.SagaEpoch != msg.ExpectedEpoch)
        {
            _logger.LogWarning(
                "Stale MarkTicketAnalyzing TicketId={TicketId} epoch={Epoch} expected={Expected}",
                msg.TicketId, ticket.SagaEpoch, msg.ExpectedEpoch);
            return;
        }

        // FAULT INJECTION (timeout verify): silent return - khong publish event nao.
        if (ticket.Question.Has(FaultInjection.ForceSkipMarkAnalyzing))
        {
            MarkAnalyzingForSaga(ticket, msg);
            await _db.SaveChangesAsync(context.CancellationToken);
            _logger.LogWarning("FaultInjection: ForceSkipMarkAnalyzing -> skip publishing event for TicketId={TicketId}", msg.TicketId);
            return;
        }

        if (IsOwnedBySaga(ticket, msg) &&
            ticket.Status is TicketStatus.Analyzing or TicketStatus.Suggested or TicketStatus.Resolved)
        {
            _logger.LogInformation("Ticket {TicketId} da {Status} (cung saga) - idempotent MarkAnalyzing.", msg.TicketId, ticket.Status);
            await context.Publish<ITicketAnalyzingMarked>(new TicketAnalyzingMarked(
                msg.CorrelationId, msg.TicketId, ticket.SagaEpoch));
            await _db.SaveChangesAsync(context.CancellationToken);
            return;
        }

        if (ticket.Status is TicketStatus.Suggested or TicketStatus.Resolved)
        {
            // Terminal tickets are idempotent success only for the saga that already owns them.
            _logger.LogWarning(
                "MarkAnalyzing fail - ticket da {Status} nhung khong thuoc saga hien tai. TicketId={TicketId} ActiveSaga={ActiveSaga} MsgSaga={MsgSaga}",
                ticket.Status,
                msg.TicketId,
                ticket.ActiveSagaCorrelationId,
                msg.CorrelationId);
            await context.Publish<ITicketAnalyzingMarkFailed>(new TicketAnalyzingMarkFailed(
                msg.CorrelationId, msg.TicketId, $"Ticket is already {ticket.Status}"));
            await _db.SaveChangesAsync(context.CancellationToken);
            return;
        }

        MarkAnalyzingForSaga(ticket, msg);

        var markedEpoch = ticket.SagaEpoch;
        await context.Publish<ITicketAnalyzingMarked>(new TicketAnalyzingMarked(
            msg.CorrelationId, msg.TicketId, markedEpoch));

        try
        {
            await _db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning("MarkAnalyzing concurrency conflict (stale epoch) TicketId={TicketId}", msg.TicketId);
            return;
        }

        _logger.LogInformation("MarkAnalyzing OK TicketId={TicketId} SagaId={SagaId} Epoch={Epoch}", msg.TicketId, msg.CorrelationId, markedEpoch);
    }

    private static bool IsOwnedBySaga(TicketEntity ticket, IMarkTicketAnalyzing msg) =>
        ticket.ActiveSagaCorrelationId == msg.CorrelationId &&
        ticket.SagaEpoch == msg.ExpectedEpoch;

    private static void MarkAnalyzingForSaga(TicketEntity ticket, IMarkTicketAnalyzing msg)
    {
        if (ticket.Status is not TicketStatus.Analyzing)
        {
            ticket.Status = TicketStatus.Analyzing;
        }

        ticket.ActiveSagaCorrelationId = msg.CorrelationId;
        ticket.SagaEpoch = msg.ExpectedEpoch + 1;
        TicketAiDraftHelper.ClearDraft(ticket);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
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
