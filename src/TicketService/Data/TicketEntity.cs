namespace SupportPoc.TicketService.Data;

public sealed class TicketEntity
{
    public required string Id { get; set; }
    public required string EmployeeId { get; set; }
    public string? OwnerOid { get; set; }
    public required string Category { get; set; }
    public required string Question { get; set; }
    public required string Status { get; set; }
    public string? AiSuggestedAnswer { get; set; }
    public string? FinalAnswer { get; set; }
    public string RelatedDocumentsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
