using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

namespace SupportPoc.TicketService.Consumers;

public sealed class SaveTicketSuggestionConsumer : IConsumer<ISaveTicketSuggestion>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TicketDbContext _db;
    private readonly ILogger<SaveTicketSuggestionConsumer> _logger;

    public SaveTicketSuggestionConsumer(TicketDbContext db, ILogger<SaveTicketSuggestionConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ISaveTicketSuggestion> context)
    {
        var msg = context.Message;
        var ticket = await _db.Tickets.FindAsync([msg.TicketId], context.CancellationToken);

        if (ticket is null)
        {
            _logger.LogWarning("SaveSuggestion fail - ticket khong ton tai. TicketId={TicketId}", msg.TicketId);
            await context.Publish<ITicketSuggestionSaveFailed>(new TicketSuggestionSaveFailed(
                msg.CorrelationId, msg.TicketId, "Ticket not found"));
            return;
        }

        if (!IsEpochValid(ticket, msg))
        {
            _logger.LogWarning(
                "Stale SaveTicketSuggestion TicketId={TicketId} epoch={Epoch} expected={Expected} activeSaga={Active}",
                msg.TicketId, ticket.SagaEpoch, msg.ExpectedEpoch, ticket.ActiveSagaCorrelationId);
            return;
        }

        if (ticket.Status == TicketStatus.Suggested && !string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer))
        {
            _logger.LogInformation("Ticket {TicketId} da co Suggestion - bo qua save lap.", msg.TicketId);
            ticket.ActiveSagaCorrelationId = null;
            await context.Publish<ITicketSuggestionSaved>(new TicketSuggestionSaved(msg.CorrelationId, msg.TicketId));
            await _db.SaveChangesAsync(context.CancellationToken);
            return;
        }

        ticket.Status = TicketStatus.Suggested;
        ticket.Category = msg.Category;
        ticket.AiSuggestedAnswer = msg.Suggestion;
        ticket.RelatedDocumentsJson = JsonSerializer.Serialize(msg.RelatedDocuments, JsonOptions);
        TicketAiDraftHelper.ClearDraft(ticket);
        ticket.ActiveSagaCorrelationId = null;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;

        // Fault injection: save da commit nhung event ack bi "mat/tre" (skip publish) de test recovery cua Saving timeout.
        var skipSavedEvent = ticket.Question.Has(FaultInjection.ForceSkipSaveSuggestionEvent);
        if (!skipSavedEvent)
        {
            await context.Publish<ITicketSuggestionSaved>(new TicketSuggestionSaved(msg.CorrelationId, msg.TicketId));
        }

        try
        {
            await _db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning(
                "SaveSuggestion concurrency conflict (compensate da bump epoch?) TicketId={TicketId}",
                msg.TicketId);
            return;
        }

        if (skipSavedEvent)
        {
            _logger.LogWarning(
                "FaultInjection: ForceSkipSaveSuggestionEvent -> saved DB but skipped TicketSuggestionSaved publish. TicketId={TicketId}",
                msg.TicketId);
            return;
        }

        _logger.LogInformation("SaveSuggestion OK TicketId={TicketId} SagaId={SagaId}", msg.TicketId, msg.CorrelationId);
    }

    private static bool IsEpochValid(TicketEntity ticket, ISaveTicketSuggestion msg) =>
        ticket.SagaEpoch == msg.ExpectedEpoch &&
        ticket.ActiveSagaCorrelationId == msg.CorrelationId;
}

public sealed class SaveTicketSuggestionConsumerDefinition : ConsumerDefinition<SaveTicketSuggestionConsumer>
{
    public SaveTicketSuggestionConsumerDefinition()
    {
        EndpointName = "save-ticket-suggestion";
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<SaveTicketSuggestionConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(100, 500, 1000, 2000));
        endpointConfigurator.UseEntityFrameworkOutbox<TicketDbContext>(context);
    }
}
