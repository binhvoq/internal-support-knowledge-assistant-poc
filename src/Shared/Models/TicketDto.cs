namespace SupportPoc.Shared.Models;

public sealed class TicketDto
{
    public required string Id { get; init; }
    public required string EmployeeId { get; init; }
    public required string Category { get; init; }
    public required string Question { get; init; }
    public required string Status { get; init; }
    public string? AiSuggestedAnswer { get; init; }
    public string? FinalAnswer { get; init; }
    public IReadOnlyList<RelatedDocument> RelatedDocuments { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public long Version { get; init; } = 1;
    public bool HasAiSuggestion => !string.IsNullOrWhiteSpace(AiSuggestedAnswer);

    /// <summary>Chi POST /tickets: true khi ticket da luu nhung dev bridge notify that bai.</summary>
    public bool AutoSuggestionNotifyFailed { get; init; }
}
