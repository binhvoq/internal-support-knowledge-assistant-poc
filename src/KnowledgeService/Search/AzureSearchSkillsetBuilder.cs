using System.Text.Json;
using SupportPoc.KnowledgeService.Options;

namespace SupportPoc.KnowledgeService.Search;

/// <summary>
/// Builds the Azure AI Search skillset payload for chunk-level index projections.
/// REST is used because Azure.Search.Documents 12.x does not expose index projections on skillset models yet.
/// </summary>
public static class AzureSearchSkillsetBuilder
{
    public static string BuildJson(AzureSearchSkillsetBuildOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SkillsetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.IndexName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OpenAiEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OpenAiApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EmbeddingDeployment);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EmbeddingModelName);

        if (options.EmbeddingDimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.EmbeddingDimensions));

        var maximumPageLength = Math.Clamp(options.ChunkMaxPageLength * 4, 300, 50000);
        var pageOverlapLength = Math.Clamp(options.ChunkPageOverlapLength * 4, 0, maximumPageLength - 1);

        var skillset = new
        {
            name = options.SkillsetName,
            description = "Extract, chunk, and embed knowledge policy documents.",
            skills = new object[]
            {
                new
                {
                    odata_type = "#Microsoft.Skills.Text.SplitSkill",
                    name = "split-policy-text",
                    description = "Chunk policy PDF/text by token-length pages (not PDF page numbers).",
                    context = "/document",
                    textSplitMode = "pages",
                    maximumPageLength,
                    pageOverlapLength,
                    inputs = new[] { new { name = "text", source = "/document/content" } },
                    outputs = new[] { new { name = "textItems", targetName = "pages" } }
                },
                new
                {
                    odata_type = "#Microsoft.Skills.Text.AzureOpenAIEmbeddingSkill",
                    name = "embed-policy-chunks",
                    description = "Create embeddings for each text chunk.",
                    context = "/document/pages/*",
                    resourceUri = options.OpenAiEndpoint,
                    apiKey = options.OpenAiApiKey,
                    deploymentId = options.EmbeddingDeployment,
                    modelName = options.EmbeddingModelName,
                    dimensions = options.EmbeddingDimensions,
                    inputs = new[] { new { name = "text", source = "/document/pages/*" } },
                    outputs = new[] { new { name = "embedding" } }
                }
            },
            indexProjections = new
            {
                selectors = new[]
                {
                    new
                    {
                        targetIndexName = options.IndexName,
                        parentKeyFieldName = KnowledgeChunkIndexFields.ParentId,
                        sourceContext = "/document/pages/*",
                        mappings = new[]
                        {
                            new { name = KnowledgeChunkIndexFields.Content, source = "/document/pages/*" },
                            new { name = KnowledgeChunkIndexFields.Embedding, source = "/document/pages/*/embedding" },
                            new { name = KnowledgeChunkIndexFields.DocumentId, source = "/document/documentid" },
                            new { name = KnowledgeChunkIndexFields.Title, source = "/document/title" },
                            new { name = KnowledgeChunkIndexFields.Category, source = "/document/category" },
                            new { name = KnowledgeChunkIndexFields.FileName, source = "/document/metadata_storage_name" },
                            new { name = KnowledgeChunkIndexFields.SourceUrl, source = "/document/metadata_storage_path" },
                            new { name = KnowledgeChunkIndexFields.UploadedAt, source = "/document/metadata_storage_last_modified" }
                        }
                    }
                },
                parameters = new { projectionMode = "skipIndexingParentDocuments" }
            }
        };

        var json = JsonSerializer.Serialize(skillset, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return json.Replace("odata_type", "@odata.type", StringComparison.Ordinal);
    }

    public static AzureSearchSkillsetBuildOptions FromConfig(
        AzureSearchOptions searchOptions,
        AzureOpenAIOptions openAiOptions) =>
        new()
        {
            SkillsetName = searchOptions.SkillsetName,
            IndexName = searchOptions.IndexName,
            ChunkMaxPageLength = searchOptions.ChunkMaxPageLength,
            ChunkPageOverlapLength = searchOptions.ChunkPageOverlapLength,
            OpenAiEndpoint = openAiOptions.Endpoint!,
            OpenAiApiKey = openAiOptions.ApiKey!,
            EmbeddingDeployment = openAiOptions.EmbeddingDeployment,
            EmbeddingModelName = openAiOptions.EmbeddingModelName,
            EmbeddingDimensions = openAiOptions.EmbeddingDimensions
        };
}

public sealed class AzureSearchSkillsetBuildOptions
{
    public required string SkillsetName { get; init; }
    public required string IndexName { get; init; }
    public required string OpenAiEndpoint { get; init; }
    public required string OpenAiApiKey { get; init; }
    public required string EmbeddingDeployment { get; init; }
    public required string EmbeddingModelName { get; init; }
    public int EmbeddingDimensions { get; init; }
    public int ChunkMaxPageLength { get; init; }
    public int ChunkPageOverlapLength { get; init; }
}
