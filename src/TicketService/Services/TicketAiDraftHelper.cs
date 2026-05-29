using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

internal static class TicketAiDraftHelper
{
    public static void ClearDraft(TicketEntity ticket)
    {
        ticket.AiDraftCategory = null;
        ticket.AiDraftSuggestion = null;
        ticket.AiDraftRelatedDocumentsJson = "[]";
        ticket.AiDraftCorrelationId = null;
        ticket.AiDraftSagaEpoch = null;
    }

    public static bool HasMatchingDraft(TicketEntity ticket, Guid correlationId, int sagaEpoch) =>
        !string.IsNullOrWhiteSpace(ticket.AiDraftSuggestion)
        && ticket.AiDraftCorrelationId == correlationId
        && ticket.AiDraftSagaEpoch == sagaEpoch;
}
