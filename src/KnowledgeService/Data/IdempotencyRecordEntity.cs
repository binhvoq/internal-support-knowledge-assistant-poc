namespace SupportPoc.KnowledgeService.Data;

public sealed class IdempotencyRecordEntity
{
    public required string Key { get; set; }
    public required string Scope { get; set; }
    public required string RequestHash { get; set; }
    public required string ResponseJson { get; set; }
    public int StatusCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
