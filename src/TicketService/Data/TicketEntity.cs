namespace SupportPoc.TicketService.Data;

public sealed class TicketEntity
{
    public required string Id { get; set; }
    public required string EmployeeId { get; set; }
    public required string Category { get; set; }
    public required string Question { get; set; }
    public required string Status { get; set; }
    public string? AiSuggestedAnswer { get; set; }
    /// <summary>AI pipeline draft (RunningAi source of truth) truoc khi SaveTicketSuggestion.</summary>
    public string? AiDraftCategory { get; set; }
    public string? AiDraftSuggestion { get; set; }
    public string AiDraftRelatedDocumentsJson { get; set; } = "[]";
    public Guid? AiDraftCorrelationId { get; set; }
    public int? AiDraftSagaEpoch { get; set; }
    public string? FinalAnswer { get; set; }
    public string RelatedDocumentsJson { get; set; } = "[]";
    /// <summary>So phien saga; tang khi Mark thanh cong va khi Compensate.</summary>
    public int SagaEpoch { get; set; }
    public Guid? ActiveSagaCorrelationId { get; set; }
    /// <summary>Ghi chu khi saga Failed va revert ticket (vd. AI timeout).</summary>
    public string? SagaStopNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
