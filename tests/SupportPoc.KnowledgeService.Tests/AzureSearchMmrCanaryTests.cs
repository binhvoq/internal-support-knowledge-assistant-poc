using System.Text.Json.Serialization;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using SupportPoc.KnowledgeService.Search;

namespace SupportPoc.KnowledgeService.Tests;

public sealed class AzureSearchMmrCanaryTests
{
    [Fact]
    public async Task Synthetic_wfh_vector_candidates_can_be_reranked_with_mmr()
    {
        await RunCanaryAsync(
            dimensions: 3,
            queryEmbedding: [1.0f, 0.8f, 0.7f],
            documents: CreateWfhDocuments(),
            assertCandidates: candidates =>
            {
                Assert.True(candidates.Count >= 5);
                Assert.True(candidates.Count(candidate => candidate.Concept == "policy") >= 2);
            },
            assertSelected: selected =>
            {
                Assert.Single(selected, candidate => candidate.Concept == "policy");
                Assert.Contains(selected, candidate => candidate.Concept == "condition");
                Assert.Contains(selected, candidate => candidate.Concept == "process");
            });
    }

    [Fact]
    public async Task Synthetic_fruit_vector_candidates_can_be_reranked_with_mmr()
    {
        await RunCanaryAsync(
            dimensions: 4,
            queryEmbedding: [1.0f, 0.8f, 0.8f, 0.0f],
            documents: CreateFruitDocuments(),
            assertCandidates: candidates =>
            {
                Assert.True(candidates.Count >= 6);
                Assert.True(candidates.Count(candidate => candidate.Concept == "orange") >= 2);
            },
            assertSelected: selected =>
            {
                Assert.Single(selected, candidate => candidate.Concept == "orange");
                Assert.Contains(selected, candidate => candidate.Concept == "watermelon");
                Assert.Contains(selected, candidate => candidate.Concept == "strawberry");
                Assert.DoesNotContain(selected, candidate => candidate.Concept == "mango");
            });
    }

    private static SearchIndex CreateCanaryIndex(string indexName, int dimensions) =>
        new(indexName)
        {
            Fields =
            [
                new SimpleField("id", SearchFieldDataType.String)
                {
                    IsKey = true,
                    IsFilterable = true
                },
                new SearchableField("content"),
                new SimpleField("concept", SearchFieldDataType.String)
                {
                    IsFilterable = true,
                    IsFacetable = true
                },
                new VectorSearchField("embedding", dimensions, "mmr-vector-profile")
            ],
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile("mmr-vector-profile", "mmr-hnsw") },
                Algorithms = { new HnswAlgorithmConfiguration("mmr-hnsw") }
            }
        };

    private static IReadOnlyList<CanarySearchDocument> CreateWfhDocuments() =>
    [
        new("wfh-2-days", "WFH toi da 2 ngay moi tuan", "policy", [1.0f, 0.0f, 0.0f]),
        new("wfh-2-days-duplicate", "Work from home 2 days per week", "policy", [0.98f, 0.04f, 0.0f]),
        new("wfh-2-days-overlap", "WFH toi da hai ngay, lap lai tu chunk overlap", "policy", [0.96f, 0.03f, 0.0f]),
        new("wfh-registration-condition", "Dieu kien dang ky WFH can bao quan ly truoc", "condition", [0.0f, 1.0f, 0.0f]),
        new("wfh-approval-process", "Quy trinh duyet WFH tren HR portal", "process", [0.0f, 0.0f, 1.0f])
    ];

    private static IReadOnlyList<CanarySearchDocument> CreateFruitDocuments() =>
    [
        new("orange-thai", "Cam Thai Lan", "orange", [1.0f, 0.0f, 0.0f, 0.0f]),
        new("orange-us", "Cam My", "orange", [0.98f, 0.03f, 0.0f, 0.0f]),
        new("orange-thai-grade-1", "Cam Thai Lan loai 1", "orange", [0.96f, 0.02f, 0.0f, 0.0f]),
        new("watermelon-red", "Dua hau ruot do", "watermelon", [0.0f, 1.0f, 0.0f, 0.0f]),
        new("strawberry", "Dau tay", "strawberry", [0.0f, 0.0f, 1.0f, 0.0f]),
        new("mango", "Xoai", "mango", [0.0f, 0.0f, 0.0f, 1.0f])
    ];

    private static async Task RunCanaryAsync(
        int dimensions,
        IReadOnlyList<float> queryEmbedding,
        IReadOnlyList<CanarySearchDocument> documents,
        Action<IReadOnlyList<CanarySearchDocument>> assertCandidates,
        Action<IReadOnlyList<CanarySearchDocument>> assertSelected)
    {
        var endpoint = ReadEnvironment("AZURE_SEARCH_ENDPOINT", "AzureSearch__Endpoint");
        var apiKey = ReadEnvironment("AZURE_SEARCH_API_KEY", "AzureSearch__ApiKey");

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            return;

        var indexName = $"mmr-canary-{Guid.NewGuid():N}";
        var credential = new AzureKeyCredential(apiKey);
        var indexClient = new SearchIndexClient(new Uri(endpoint), credential);

        try
        {
            await indexClient.CreateIndexAsync(CreateCanaryIndex(indexName, dimensions));
            var searchClient = new SearchClient(new Uri(endpoint), indexName, credential);

            await searchClient.UploadDocumentsAsync(documents);
            await WaitUntilSearchableAsync(searchClient, documents.Count);

            var vectorQuery = new VectorizedQuery(queryEmbedding.ToArray())
            {
                KNearestNeighborsCount = documents.Count,
                Fields = { "embedding" }
            };
            var options = new SearchOptions
            {
                Size = documents.Count,
                VectorSearch = new VectorSearchOptions { Queries = { vectorQuery } },
                Select = { "id", "content", "concept", "embedding" }
            };

            var response = await searchClient.SearchAsync<CanarySearchDocument>(null, options);
            var candidates = new List<CanarySearchDocument>();
            await foreach (var result in response.Value.GetResultsAsync())
            {
                if (result.Document.Embedding.Count > 0)
                    candidates.Add(result.Document);
            }

            assertCandidates(candidates);

            var selected = new MmrReranker().Select(
                candidates,
                queryEmbedding,
                candidate => candidate.Embedding,
                top: 3,
                lambda: 0.7);

            assertSelected(selected);
        }
        finally
        {
            try
            {
                await indexClient.DeleteIndexAsync(indexName, CancellationToken.None);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
            }
        }
    }

    private static async Task WaitUntilSearchableAsync(SearchClient searchClient, int expectedCount)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var response = await searchClient.SearchAsync<CanarySearchDocument>(
                "*",
                new SearchOptions { Size = 0, IncludeTotalCount = true });

            if ((response.Value.TotalCount ?? 0) >= expectedCount)
                return;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new InvalidOperationException("Canary documents were not searchable within the expected time.");
    }

    private static string? ReadEnvironment(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private sealed record CanarySearchDocument(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("concept")] string Concept,
        [property: JsonPropertyName("embedding")] IReadOnlyList<float> Embedding);
}
