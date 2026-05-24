namespace SupportPoc.KnowledgeService.Options;

public sealed class AzureStorageOptions
{
    public const string SectionName = "AzureStorage";
    public string? ConnectionString { get; set; }
    public string ContainerName { get; set; } = "knowledge-docs";
    public bool Enabled => !string.IsNullOrWhiteSpace(ConnectionString);
}
