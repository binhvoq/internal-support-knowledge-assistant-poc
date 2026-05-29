using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared.Contracts;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Consumers;

public sealed class RecordAiPipelineDraftConsumer : IConsumer<IRecordAiPipelineDraft>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TicketDbContext _db;
    private readonly ILogger<RecordAiPipelineDraftConsumer> _logger;

    public RecordAiPipelineDraftConsumer(TicketDbContext db, ILogger<RecordAiPipelineDraftConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<IRecordAiPipelineDraft> context)
    {
        var msg = context.Message;
        var ticket = await _db.Tickets.FindAsync([msg.TicketId], context.CancellationToken);

        if (ticket is null)
        {
            _logger.LogWarning("RecordAiDraft reject - ticket not found. TicketId={TicketId}", msg.TicketId);
            await context.RespondAsync<IAiPipelineDraftRejected>(new AiPipelineDraftRejected(
                msg.CorrelationId, msg.TicketId, "Ticket not found"));
            return;
        }

        if (ticket.SagaEpoch != msg.ExpectedEpoch || ticket.ActiveSagaCorrelationId != msg.CorrelationId)
        {
            _logger.LogWarning(
                "RecordAiDraft reject - stale ownership. TicketId={TicketId} epoch={Epoch} expected={Expected} active={Active}",
                msg.TicketId,
                ticket.SagaEpoch,
                msg.ExpectedEpoch,
                ticket.ActiveSagaCorrelationId);
            await context.RespondAsync<IAiPipelineDraftRejected>(new AiPipelineDraftRejected(
                msg.CorrelationId,
                msg.TicketId,
                $"Stale epoch or saga ownership (epoch={ticket.SagaEpoch}, active={ticket.ActiveSagaCorrelationId})"));
            return;
        }

        if (TicketAiDraftHelper.HasMatchingDraft(ticket, msg.CorrelationId, msg.ExpectedEpoch))
        {
            _logger.LogInformation(
                "RecordAiDraft idempotent OK TicketId={TicketId} SagaId={SagaId}",
                msg.TicketId,
                msg.CorrelationId);
            await context.RespondAsync<IAiPipelineDraftRecorded>(new AiPipelineDraftRecorded(msg.CorrelationId, msg.TicketId));
            return;
        }

        ticket.AiDraftCategory = msg.Category;
        ticket.AiDraftSuggestion = msg.Suggestion;
        ticket.AiDraftRelatedDocumentsJson = JsonSerializer.Serialize(msg.RelatedDocuments, JsonOptions);
        ticket.AiDraftCorrelationId = msg.CorrelationId;
        ticket.AiDraftSagaEpoch = msg.ExpectedEpoch;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await _db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning("RecordAiDraft concurrency conflict TicketId={TicketId}", msg.TicketId);
            await context.RespondAsync<IAiPipelineDraftRejected>(new AiPipelineDraftRejected(
                msg.CorrelationId, msg.TicketId, "Concurrency conflict"));
            return;
        }

        _logger.LogInformation(
            "RecordAiDraft OK TicketId={TicketId} SagaId={SagaId} Epoch={Epoch}",
            msg.TicketId,
            msg.CorrelationId,
            msg.ExpectedEpoch);

        await context.RespondAsync<IAiPipelineDraftRecorded>(new AiPipelineDraftRecorded(msg.CorrelationId, msg.TicketId));
    }
}

public sealed class RecordAiPipelineDraftConsumerDefinition : ConsumerDefinition<RecordAiPipelineDraftConsumer>
{
    public RecordAiPipelineDraftConsumerDefinition()
    {
        EndpointName = "record-ai-pipeline-draft";
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<RecordAiPipelineDraftConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000, 2000));
        endpointConfigurator.UseEntityFrameworkOutbox<TicketDbContext>(context);
    }
}
