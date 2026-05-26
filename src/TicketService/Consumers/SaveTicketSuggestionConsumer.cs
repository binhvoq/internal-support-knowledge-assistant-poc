using System.Text.Json;
using MassTransit;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;

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

        // State-based idempotency: chi save khi dang Analyzing.
        // Neu da Suggested roi -> coi nhu thanh cong (retry case).
        if (ticket.Status == TicketStatus.Suggested && !string.IsNullOrWhiteSpace(ticket.AiSuggestedAnswer))
        {
            _logger.LogInformation("Ticket {TicketId} da co Suggestion - bo qua save lap.", msg.TicketId);
            await context.Publish<ITicketSuggestionSaved>(new TicketSuggestionSaved(msg.CorrelationId, msg.TicketId));
            return;
        }

        ticket.Status = TicketStatus.Suggested;
        ticket.Category = msg.Category;
        ticket.AiSuggestedAnswer = msg.Suggestion;
        ticket.RelatedDocumentsJson = JsonSerializer.Serialize(msg.RelatedDocuments, JsonOptions);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;

        await context.Publish<ITicketSuggestionSaved>(new TicketSuggestionSaved(msg.CorrelationId, msg.TicketId));
        await _db.SaveChangesAsync(context.CancellationToken);

        _logger.LogInformation("SaveSuggestion OK TicketId={TicketId} SagaId={SagaId}", msg.TicketId, msg.CorrelationId);
    }
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
