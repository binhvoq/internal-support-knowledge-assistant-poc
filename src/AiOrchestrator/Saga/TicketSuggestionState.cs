using MassTransit;

namespace SupportPoc.AiOrchestrator.Saga;

// Saga instance state - duoc persist boi MassTransit EntityFrameworkSagaRepository.
// Mot row trong bang TicketSuggestionState ung voi mot saga dang chay.
// MassTransit dung CorrelationId lam khoa chinh va Version cho optimistic concurrency.
public sealed class TicketSuggestionState : SagaStateMachineInstance, ISagaVersion
{
    // PK - moi message phai mang CorrelationId nay de dinh tuyen den dung saga instance.
    public Guid CorrelationId { get; set; }

    // Version dung cho optimistic concurrency - tranh "lost update" khi
    // 2 message ve cung saga arrive cung luc.
    public int Version { get; set; }

    public string? CurrentState { get; set; }

    public required string TicketId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;

    // Trang thai ticket truoc khi MarkAnalyzing - de compensate chinh xac.
    public string OriginalStatus { get; set; } = string.Empty;

    public string? Category { get; set; }
    public string? Suggestion { get; set; }
    public string? RelatedDocumentsJson { get; set; }

    // Token de Unschedule timeout khi saga ket thuc binh thuong.
    public Guid? TimeoutTokenId { get; set; }

    public string? FailureReason { get; set; }
    public string? CompensationReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
