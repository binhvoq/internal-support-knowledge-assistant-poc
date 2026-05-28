namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;

public sealed record TicketProgressSnapshot(
    string TicketId,
    string Status,
    int SagaEpoch,
    Guid? ActiveSagaCorrelationId,
    bool HasSuggestion);
