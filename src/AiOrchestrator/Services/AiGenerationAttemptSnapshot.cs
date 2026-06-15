namespace SupportPoc.AiOrchestrator.Services;

public sealed record AiGenerationAttemptSnapshot(
    Guid AttemptId,
    string Status,
    DateTimeOffset? LeaseUntil,
    DateTimeOffset StartedAt,
    string? Category,
    string? Suggestion,
    string RelatedDocumentsJson,
    string? Error);
