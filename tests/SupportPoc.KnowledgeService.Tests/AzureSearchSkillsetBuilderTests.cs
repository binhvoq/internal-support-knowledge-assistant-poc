using System.Text.Json;
using SupportPoc.KnowledgeService.Options;
using SupportPoc.KnowledgeService.Search;

namespace SupportPoc.KnowledgeService.Tests;

public sealed class AzureSearchSkillsetBuilderTests
{
    private static AzureSearchSkillsetBuildOptions SampleOptions() => new()
    {
        SkillsetName = "knowledge-pdf-skillset",
        IndexName = "knowledge-chunks",
        OpenAiEndpoint = "https://example.openai.azure.com/",
        OpenAiApiKey = "test-key",
        EmbeddingDeployment = "text-embedding-3-small",
        EmbeddingModelName = "text-embedding-ada-002",
        EmbeddingDimensions = 3072,
        ChunkMaxPageLength = 1000,
        ChunkPageOverlapLength = 200
    };

    [Fact]
    public void BuildJson_uses_parentId_not_chunkId_as_parentKeyFieldName()
    {
        var json = AzureSearchSkillsetBuilder.BuildJson(SampleOptions());
        using var doc = JsonDocument.Parse(json);

        var parentKey = doc.RootElement
            .GetProperty("indexProjections")
            .GetProperty("selectors")[0]
            .GetProperty("parentKeyFieldName")
            .GetString();

        Assert.Equal(KnowledgeChunkIndexFields.ParentId, parentKey);
        Assert.DoesNotContain("\"parentKeyFieldName\":\"chunkId\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildJson_does_not_map_fake_ordinalPosition_paths()
    {
        var json = AzureSearchSkillsetBuilder.BuildJson(SampleOptions());

        Assert.DoesNotContain("ordinalPosition", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNumber", json, StringComparison.Ordinal);
        Assert.DoesNotContain("chunkIndex", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildJson_maps_blob_custom_metadata_with_verbatim_paths()
    {
        var json = AzureSearchSkillsetBuilder.BuildJson(SampleOptions());

        Assert.Contains("\"source\":\"/document/documentid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"/document/title\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"/document/category\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/document/metadata_documentid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/document/metadata_title", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/document/metadata_category", json, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildJson_uses_embedding_model_and_dimensions_from_options()
    {
        var json = AzureSearchSkillsetBuilder.BuildJson(SampleOptions());
        using var doc = JsonDocument.Parse(json);

        var embedSkill = doc.RootElement.GetProperty("skills")[1];
        Assert.Equal("text-embedding-ada-002", embedSkill.GetProperty("modelName").GetString());
        Assert.Equal(3072, embedSkill.GetProperty("dimensions").GetInt32());
    }

    [Fact]
    public void FromConfig_maps_openAi_and_search_options()
    {
        var json = AzureSearchSkillsetBuilder.BuildJson(AzureSearchSkillsetBuilder.FromConfig(
            new AzureSearchOptions { SkillsetName = "ss", IndexName = "idx", ChunkMaxPageLength = 800, ChunkPageOverlapLength = 100 },
            new AzureOpenAIOptions
            {
                Endpoint = "https://oai.example/",
                ApiKey = "k",
                EmbeddingDeployment = "embed-deploy",
                EmbeddingModelName = "custom-embed",
                EmbeddingDimensions = 1536
            }));

        Assert.Contains("custom-embed", json, StringComparison.Ordinal);
        Assert.Contains("embed-deploy", json, StringComparison.Ordinal);
        Assert.Contains("\"dimensions\":1536", json, StringComparison.Ordinal);
    }
}
