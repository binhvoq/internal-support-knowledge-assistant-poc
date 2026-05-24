using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using SupportPoc.KnowledgeService.Data;
using SupportPoc.KnowledgeService.Options;
using SupportPoc.Shared.Models;

namespace SupportPoc.KnowledgeService.Search;

public sealed class KnowledgeSearchService
{
    private readonly AzureSearchOptions _options;
    private readonly ILogger<KnowledgeSearchService> _logger;

    public KnowledgeSearchService(IOptions<AzureSearchOptions> options, ILogger<KnowledgeSearchService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureIndexAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        var indexClient = new SearchIndexClient(new Uri(_options.Endpoint!), new AzureKeyCredential(_options.ApiKey!));
        var definition = new SearchIndex(_options.IndexName)
        {
            Fields =
            [
                new SimpleField("documentId", SearchFieldDataType.String) { IsKey = true },
                new SearchableField("title") { IsFilterable = true },
                new SearchableField("category") { IsFilterable = true },
                new SearchableField("content"),
                new SimpleField("sourceUrl", SearchFieldDataType.String),
                new SimpleField("updatedAt", SearchFieldDataType.DateTimeOffset) { IsSortable = true, IsFilterable = true },
                new VectorSearchField("embedding", 1536, "vector-profile")
            ],
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile("vector-profile", "hnsw-config") },
                Algorithms = { new HnswAlgorithmConfiguration("hnsw-config") }
            }
        };

        try
        {
            await indexClient.CreateOrUpdateIndexAsync(definition, cancellationToken: cancellationToken);
            _logger.LogInformation("Azure AI Search index {Index} san sang.", _options.IndexName);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _logger.LogInformation("Index {Index} da ton tai.", _options.IndexName);
        }
    }

    public async Task UpsertDocumentsAsync(IEnumerable<(KnowledgeDocumentEntity Entity, IReadOnlyList<float>? Embedding)> docs, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        var client = CreateSearchClient();
        var batch = docs.Select(d => new KnowledgeSearchDocument
        {
            DocumentId = d.Entity.Id,
            Title = d.Entity.Title,
            Category = d.Entity.Category,
            Content = d.Entity.Content,
            SourceUrl = d.Entity.SourceUrl,
            UpdatedAt = d.Entity.UpdatedAt,
            Embedding = d.Embedding
        }).ToList();

        await client.MergeOrUploadDocumentsAsync(batch, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<RelatedDocument>> SearchAsync(string query, string? category, int top = 5, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return [];

        var client = CreateSearchClient();
        var options = new SearchOptions
        {
            Size = top,
            Select = { "documentId", "title", "content" },
            SearchFields = { "title", "content" }
        };
        if (!string.IsNullOrWhiteSpace(category))
            options.Filter = $"category eq '{category.Replace("'", "''")}'";

        return await ReadHitsAsync(await client.SearchAsync<KnowledgeSearchHit>(query, options, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<RelatedDocument>> VectorSearchAsync(
        IReadOnlyList<float> embedding, string? category, int top = 5, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return [];

        var client = CreateSearchClient();
        var vectorQuery = new VectorizedQuery(embedding.ToArray()) { KNearestNeighborsCount = top, Fields = { "embedding" } };
        var options = new SearchOptions
        {
            Size = top,
            Select = { "documentId", "title", "content" },
            VectorSearch = new VectorSearchOptions { Queries = { vectorQuery } }
        };
        if (!string.IsNullOrWhiteSpace(category))
            options.Filter = $"category eq '{category.Replace("'", "''")}'";

        return await ReadHitsAsync(await client.SearchAsync<KnowledgeSearchHit>(null, options, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<RelatedDocument>> HybridSearchAsync(
        string query, IReadOnlyList<float> embedding, string? category, int top = 5, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return [];

        var client = CreateSearchClient();
        var vectorQuery = new VectorizedQuery(embedding.ToArray()) { KNearestNeighborsCount = top, Fields = { "embedding" } };
        var options = new SearchOptions
        {
            Size = top,
            Select = { "documentId", "title", "content" },
            SearchFields = { "title", "content" },
            VectorSearch = new VectorSearchOptions { Queries = { vectorQuery } }
        };
        if (!string.IsNullOrWhiteSpace(category))
            options.Filter = $"category eq '{category.Replace("'", "''")}'";

        return await ReadHitsAsync(await client.SearchAsync<KnowledgeSearchHit>(query, options, cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<RelatedDocument>> ReadHitsAsync(
        Azure.Response<SearchResults<KnowledgeSearchHit>> response,
        CancellationToken cancellationToken)
    {
        var results = new List<RelatedDocument>();
        await foreach (var item in response.Value.GetResultsAsync())
        {
            if (string.IsNullOrWhiteSpace(item.Document.DocumentId)) continue;
            results.Add(new RelatedDocument
            {
                DocumentId = item.Document.DocumentId!,
                Title = item.Document.Title ?? item.Document.DocumentId!,
                Content = item.Document.Content,
                Score = item.Score ?? 0
            });
        }
        return results;
    }

    private SearchClient CreateSearchClient() =>
        new(new Uri(_options.Endpoint!), _options.IndexName, new AzureKeyCredential(_options.ApiKey!));
}
