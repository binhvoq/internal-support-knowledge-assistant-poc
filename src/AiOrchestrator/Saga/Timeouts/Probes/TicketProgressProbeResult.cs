namespace SupportPoc.AiOrchestrator.Saga.Timeouts.Probes;

public sealed record TicketProgressProbeResult(
    TicketProgressProbeStatus Status,
    TicketProgressSnapshot? Snapshot,
    string? Error);
