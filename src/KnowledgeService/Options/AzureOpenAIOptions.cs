namespace SupportPoc.KnowledgeService.Options;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";
    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);
}
