using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Options;
using SupportPoc.KnowledgeService.Options;
using SupportPoc.Shared.Models;

namespace SupportPoc.KnowledgeService.Search;

public sealed class KnowledgeSearchService
{
    private readonly AzureSearchOptions _options;
    private readonly AzureOpenAIOptions _openAiOptions;
    private readonly ILogger<KnowledgeSearchService> _logger;

    public KnowledgeSearchService(
        IOptions<AzureSearchOptions> options,
        IOptions<AzureOpenAIOptions> openAiOptions,
        ILogger<KnowledgeSearchService> logger)
    {
        _options = options.Value;
        _openAiOptions = openAiOptions.Value;
        _logger = logger;
    }

    public async Task EnsureChunkIndexAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        var dimensions = _openAiOptions.EmbeddingDimensions;
        if (dimensions <= 0)
            throw new InvalidOperationException("AzureOpenAI:EmbeddingDimensions phai lon hon 0.");

        var indexClient = CreateIndexClient();
        var definition = new SearchIndex(_options.IndexName)
        {
            Fields =
            [
                new SearchField(KnowledgeChunkIndexFields.ChunkId, SearchFieldDataType.String)
                {
                    IsKey = true,
                    IsSearchable = true,
                    AnalyzerName = LexicalAnalyzerName.Values.Keyword
                },
                new SimpleField(KnowledgeChunkIndexFields.ParentId, SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField(KnowledgeChunkIndexFields.DocumentId, SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SearchableField(KnowledgeChunkIndexFields.Title) { IsFilterable = true, IsFacetable = true },
                new SimpleField(KnowledgeChunkIndexFields.Category, SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
                new SimpleField(KnowledgeChunkIndexFields.FileName, SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField(KnowledgeChunkIndexFields.SourceUrl, SearchFieldDataType.String),
                new SearchableField(KnowledgeChunkIndexFields.Content),
                new SimpleField(KnowledgeChunkIndexFields.UploadedAt, SearchFieldDataType.DateTimeOffset) { IsSortable = true, IsFilterable = true },
                new VectorSearchField(KnowledgeChunkIndexFields.Embedding, dimensions, KnowledgeChunkIndexFields.VectorProfile)
            ],
            VectorSearch = new VectorSearch
            {
                Profiles = { new VectorSearchProfile(KnowledgeChunkIndexFields.VectorProfile, "hnsw-config") },
                Algorithms = { new HnswAlgorithmConfiguration("hnsw-config") }
            }
        };

        if (_options.SemanticRankerEnabled)
        {
            definition.SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration(_options.SemanticConfigurationName, new SemanticPrioritizedFields
                    {
                        TitleField = new SemanticField(KnowledgeChunkIndexFields.Title),
                        ContentFields = { new SemanticField(KnowledgeChunkIndexFields.Content) }
                    })
                }
            };
        }

        await indexClient.CreateOrUpdateIndexAsync(definition, cancellationToken: cancellationToken);
        _logger.LogInformation(
            "Azure AI Search chunk index {Index} san sang (embedding dimensions={Dimensions}).",
            _options.IndexName,
            dimensions);
    }

    public async Task<int> CountChunksByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return 0;

        var client = CreateSearchClient();
        var options = new SearchOptions
        {
            Filter = BuildDocumentFilter(documentId),
            Size = 0,
            IncludeTotalCount = true
        };

        var response = await client.SearchAsync<KnowledgeChunkSearchHit>(null, options, cancellationToken);
        return (int)(response.Value.TotalCount ?? 0);
    }

    public async Task DeleteChunksByDocumentIdAsync(string documentId, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return;

        var client = CreateSearchClient();
        var chunkIds = new List<string>();
        var options = new SearchOptions
        {
            Filter = BuildDocumentFilter(documentId),
            Select = { KnowledgeChunkIndexFields.ChunkId },
            Size = 1000
        };

        while (true)
        {
            options.Skip = chunkIds.Count;
            var response = await client.SearchAsync<KnowledgeChunkSearchHit>(null, options, cancellationToken);
            var pageCount = 0;
            await foreach (var item in response.Value.GetResultsAsync())
            {
                pageCount++;
                if (!string.IsNullOrWhiteSpace(item.Document.ChunkId))
                    chunkIds.Add(item.Document.ChunkId!);
            }

            if (pageCount < options.Size)
                break;
        }

        if (chunkIds.Count == 0)
            return;

        foreach (var batch in chunkIds.Chunk(1000))
        {
            var documents = batch.Select(id => new KnowledgeChunkSearchDocument
            {
                ChunkId = id,
                DocumentId = documentId,
                Title = string.Empty,
                Category = string.Empty,
                Content = string.Empty,
                UploadedAt = DateTimeOffset.UtcNow
            }).ToList();

            await client.DeleteDocumentsAsync(documents, cancellationToken: cancellationToken);
        }

        _logger.LogInformation("Da xoa {Count} chunk cho document {DocumentId}.", chunkIds.Count, documentId);
    }

    public async Task<IReadOnlyList<RelatedDocument>> SearchAsync(
        string query,
        IReadOnlyList<float>? embedding,
        string? category,
        KnowledgeSearchMode mode,
        KnowledgeRerankMethod rerankMethod,
        int top = 5,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) return [];
        if (top <= 0) return [];

        if (mode is KnowledgeSearchMode.Vector or KnowledgeSearchMode.Hybrid || rerankMethod == KnowledgeRerankMethod.Mmr)
        {
            if (embedding is null)
                throw new InvalidOperationException($"{mode} search voi {rerankMethod} rerank can query embedding.");

            ValidateQueryEmbedding(embedding);
        }

        if (rerankMethod == KnowledgeRerankMethod.AzureSemantic && mode == KnowledgeSearchMode.Vector)
            throw new InvalidOperationException("Azure Semantic Ranker can text query; khong ho tro vector-only mode.");

        if (rerankMethod == KnowledgeRerankMethod.AzureSemantic && !_options.SemanticRankerEnabled)
            throw new InvalidOperationException("Azure Semantic Ranker chua duoc enable trong config.");

        var client = CreateSearchClient();
        var useMmr = rerankMethod == KnowledgeRerankMethod.Mmr;
        var candidateTop = useMmr ? BuildCandidateTop(top) : top;
        var options = BuildSearchOptions(category, candidateTop);
        var searchText = ConfigureSearchOptions(options, query, embedding, mode, candidateTop, useMmr);

        if (rerankMethod == KnowledgeRerankMethod.AzureSemantic)
        {
            options.QueryType = SearchQueryType.Semantic;
            options.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = _options.SemanticConfigurationName
            };
        }

        var candidates = await ReadCandidateHitsAsync(
            await client.SearchAsync<KnowledgeChunkSearchHit>(searchText, options, cancellationToken),
            cancellationToken);

        if (!useMmr || candidates.Count <= top)
            return candidates.Select(candidate => candidate.Document).Take(top).ToList();

        var missingEmbeddingCount = candidates.Count(candidate => candidate.Embedding is null);
        if (missingEmbeddingCount > 0)
        {
            _logger.LogWarning(
                "Bo qua MMR rerank vi {MissingEmbeddingCount}/{CandidateCount} candidate thieu embedding.",
                missingEmbeddingCount,
                candidates.Count);
            return candidates.Select(candidate => candidate.Document).Take(top).ToList();
        }

        var reranked = new MmrReranker().Select(
            candidates,
            embedding!,
            candidate => candidate.Embedding!,
            top,
            _options.MmrLambda);

        return reranked.Select(candidate => candidate.Document).ToList();
    }

    private void ValidateQueryEmbedding(IReadOnlyList<float> embedding)
    {
        if (embedding.Count != _openAiOptions.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Query embedding co {embedding.Count} dimensions, khong khop config {_openAiOptions.EmbeddingDimensions}.");
        }
    }

    private SearchOptions BuildSearchOptions(string? category, int top)
    {
        var options = new SearchOptions
        {
            Size = top,
            Select =
            {
                KnowledgeChunkIndexFields.ChunkId,
                KnowledgeChunkIndexFields.ParentId,
                KnowledgeChunkIndexFields.DocumentId,
                KnowledgeChunkIndexFields.Title,
                KnowledgeChunkIndexFields.Category,
                KnowledgeChunkIndexFields.FileName,
                KnowledgeChunkIndexFields.SourceUrl,
                KnowledgeChunkIndexFields.Content,
                KnowledgeChunkIndexFields.UploadedAt
            }
        };

        if (!string.IsNullOrWhiteSpace(category))
            options.Filter = $"{KnowledgeChunkIndexFields.Category} eq '{EscapeOData(category)}'";

        return options;
    }

    private int BuildCandidateTop(int top)
    {
        if (!_options.MmrRerankingEnabled)
            return top;

        var candidateTop = Math.Max(top, _options.MmrCandidateTop);
        return Math.Clamp(candidateTop, top, 100);
    }

    private static string? ConfigureSearchOptions(
        SearchOptions options,
        string query,
        IReadOnlyList<float>? embedding,
        KnowledgeSearchMode mode,
        int candidateTop,
        bool includeEmbedding)
    {
        if (includeEmbedding)
            options.Select.Add(KnowledgeChunkIndexFields.Embedding);

        switch (mode)
        {
            case KnowledgeSearchMode.Keyword:
                options.SearchFields.Add(KnowledgeChunkIndexFields.Title);
                options.SearchFields.Add(KnowledgeChunkIndexFields.Content);
                return query;

            case KnowledgeSearchMode.Vector:
                options.VectorSearch = new VectorSearchOptions
                {
                    Queries =
                    {
                        new VectorizedQuery(embedding!.ToArray())
                        {
                            KNearestNeighborsCount = candidateTop,
                            Fields = { KnowledgeChunkIndexFields.Embedding }
                        }
                    }
                };
                return null;

            case KnowledgeSearchMode.Hybrid:
                options.SearchFields.Add(KnowledgeChunkIndexFields.Title);
                options.SearchFields.Add(KnowledgeChunkIndexFields.Content);
                options.VectorSearch = new VectorSearchOptions
                {
                    Queries =
                    {
                        new VectorizedQuery(embedding!.ToArray())
                        {
                            KNearestNeighborsCount = candidateTop,
                            Fields = { KnowledgeChunkIndexFields.Embedding }
                        }
                    }
                };
                return query;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported search mode.");
        }
    }

    private static async Task<IReadOnlyList<KnowledgeSearchCandidate>> ReadCandidateHitsAsync(
        Response<SearchResults<KnowledgeChunkSearchHit>> response,
        CancellationToken cancellationToken)
    {
        var results = new List<KnowledgeSearchCandidate>();
        await foreach (var item in response.Value.GetResultsAsync())
        {
            var doc = item.Document;
            if (string.IsNullOrWhiteSpace(doc.DocumentId)) continue;

            var related = new RelatedDocument
            {
                ChunkId = doc.ChunkId,
                ParentId = doc.ParentId,
                DocumentId = doc.DocumentId!,
                Title = doc.Title ?? doc.DocumentId!,
                Content = doc.Content,
                Score = item.Score ?? 0,
                FileName = doc.FileName
            };

            results.Add(new KnowledgeSearchCandidate(related, doc.Embedding));
        }

        return results;
    }

    private static string BuildDocumentFilter(string documentId) =>
        $"{KnowledgeChunkIndexFields.DocumentId} eq '{EscapeOData(documentId)}'";

    private static string EscapeOData(string value) => value.Replace("'", "''");

    private SearchClient CreateSearchClient() =>
        new(new Uri(_options.Endpoint!), _options.IndexName, new AzureKeyCredential(_options.ApiKey!));

    private SearchIndexClient CreateIndexClient() =>
        new(new Uri(_options.Endpoint!), new AzureKeyCredential(_options.ApiKey!));

    private sealed record KnowledgeSearchCandidate(RelatedDocument Document, IReadOnlyList<float>? Embedding);
}
