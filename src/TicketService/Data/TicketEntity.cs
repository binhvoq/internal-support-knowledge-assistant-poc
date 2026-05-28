namespace SupportPoc.TicketService.Data;

public sealed class TicketEntity
{
    public required string Id { get; set; }
    public required string EmployeeId { get; set; }
    public required string Category { get; set; }
    public required string Question { get; set; }
    public required string Status { get; set; }
    public string? AiSuggestedAnswer { get; set; }
    public string? FinalAnswer { get; set; }
    public string RelatedDocumentsJson { get; set; } = "[]";
    /// <summary>So phien saga; tang khi Mark thanh cong va khi Compensate.</summary>
    public int SagaEpoch { get; set; }
    public Guid? ActiveSagaCorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
