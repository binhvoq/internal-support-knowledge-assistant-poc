namespace SupportPoc.KnowledgeService.Options;

public sealed class AzureSearchOptions
{
    public const string SectionName = "AzureSearch";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string IndexName { get; set; } = "knowledge-documents";
    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);
}
