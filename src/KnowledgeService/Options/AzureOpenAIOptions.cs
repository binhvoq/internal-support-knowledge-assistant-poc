namespace SupportPoc.KnowledgeService.Options;

public sealed class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";
    public string EmbeddingModelName { get; set; } = "text-embedding-3-small";
    public int EmbeddingDimensions { get; set; } = 1536;
    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);
}
