namespace SupportPoc.KnowledgeService.Options;

public sealed class AzureSearchOptions
{
    public const string SectionName = "AzureSearch";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string IndexName { get; set; } = "knowledge-chunks";
    public string DataSourceName { get; set; } = "knowledge-blob-datasource";
    public string SkillsetName { get; set; } = "knowledge-pdf-skillset";
    public string IndexerName { get; set; } = "knowledge-blob-indexer";
    public bool SemanticRankerEnabled { get; set; }
    public string SemanticConfigurationName { get; set; } = "knowledge-semantic";
    public int ChunkMaxPageLength { get; set; } = 1000;
    public int ChunkPageOverlapLength { get; set; } = 200;
    public bool MmrRerankingEnabled { get; set; } = true;
    public int MmrCandidateTop { get; set; } = 20;
    public double MmrLambda { get; set; } = 0.5;
    public int IngestionPollTimeoutSeconds { get; set; } = 120;
    public int IngestionRefreshIntervalSeconds { get; set; } = 30;
    /// <summary>Azure Search admin REST API version for skillset index projections.</summary>
    public string AdminApiVersion { get; set; } = "2024-07-01";
    public bool Enabled => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey);
}
