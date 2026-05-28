using System.Text.Json;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Saga.Timeouts;

public static class SagaSaveCommandFactory
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool CanCreate(TicketSuggestionState saga, out string reason)
    {
        if (string.IsNullOrWhiteSpace(saga.Category) || string.IsNullOrWhiteSpace(saga.Suggestion))
        {
            reason = "Cannot resend save: saga missing Category or Suggestion.";
            return false;
        }

        try
        {
            _ = DeserializeRelatedDocuments(saga.RelatedDocumentsJson);
        }
        catch (InvalidOperationException ex)
        {
            reason = ex.Message;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static SaveTicketSuggestion Create(TicketSuggestionState saga)
    {
        if (string.IsNullOrWhiteSpace(saga.Category) || string.IsNullOrWhiteSpace(saga.Suggestion))
            throw new InvalidOperationException("Cannot resend save: saga missing Category or Suggestion.");

        var related = DeserializeRelatedDocuments(saga.RelatedDocumentsJson);

        return new SaveTicketSuggestion(
            saga.CorrelationId,
            saga.TicketId,
            saga.TicketSagaEpoch,
            saga.Category,
            saga.Suggestion,
            related);
    }

    public static IReadOnlyList<RelatedDocument> DeserializeRelatedDocuments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<RelatedDocument>();

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<RelatedDocument>>(json, JsonOptions)
                   ?? Array.Empty<RelatedDocument>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Cannot resend save: RelatedDocumentsJson is invalid.", ex);
        }
    }
}
