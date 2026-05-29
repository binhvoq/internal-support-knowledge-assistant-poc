namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;

public sealed record TicketProgressSnapshot(
    string TicketId,
    string Status,
    int SagaEpoch,
    Guid? ActiveSagaCorrelationId,
    bool HasSuggestion,
    bool HasAiDraft = false,
    Guid? AiDraftCorrelationId = null,
    int? AiDraftSagaEpoch = null,
    string? AiDraftCategory = null,
    string? AiDraftSuggestion = null,
    string? AiDraftRelatedDocumentsJson = null);
