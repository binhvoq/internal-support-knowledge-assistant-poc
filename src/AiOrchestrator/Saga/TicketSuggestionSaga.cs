using MassTransit;

namespace SupportPoc.AiOrchestrator.Saga;

public sealed class TicketSuggestionSaga : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [0, 0, 0, 0, 0, 0, 0, 1];

    public string TicketId { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string OriginalCategory { get; set; } = string.Empty;

    public Guid JobId { get; set; }
    public Guid CurrentAttemptId { get; set; }
    public int RetryCount { get; set; }
    public int ProposeRetryCount { get; set; }

    public long? TicketVersionAtStart { get; set; }
    public Guid? StepTimeoutTokenId { get; set; }
    public Guid? LastProposeCommandId { get; set; }

    public string? GeneratedCategory { get; set; }
    public string? GeneratedSuggestion { get; set; }
    public string GeneratedRelatedDocumentsJson { get; set; } = "[]";

    public string? FailureReason { get; set; }
    public string? DiscardReason { get; set; }
    public string? LateMessageAudit { get; set; }
    public string? PendingReconcileAction { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
