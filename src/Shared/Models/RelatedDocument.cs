namespace SupportPoc.Shared.Models;

public sealed class RelatedDocument
{
    public required string DocumentId { get; init; }
    public required string Title { get; init; }
    public string? Content { get; init; }
    public double Score { get; init; }
}
