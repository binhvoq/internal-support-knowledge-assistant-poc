using System.Text;
using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Options;
using SupportPoc.KnowledgeService.Options;

namespace SupportPoc.KnowledgeService.Search;

public sealed class AzureSearchIngestionService
{
    private readonly AzureSearchOptions _searchOptions;
    private readonly AzureOpenAIOptions _openAiOptions;
    private readonly AzureStorageOptions _storageOptions;
    private readonly ILogger<AzureSearchIngestionService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public AzureSearchIngestionService(
        IOptions<AzureSearchOptions> searchOptions,
        IOptions<AzureOpenAIOptions> openAiOptions,
        IOptions<AzureStorageOptions> storageOptions,
        IHttpClientFactory httpClientFactory,
        ILogger<AzureSearchIngestionService> logger)
    {
        _searchOptions = searchOptions.Value;
        _openAiOptions = openAiOptions.Value;
        _storageOptions = storageOptions.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsPipelineConfigured =>
        _searchOptions.Enabled && _storageOptions.Enabled && _openAiOptions.Enabled;

    public async Task EnsurePipelineAsync(CancellationToken cancellationToken = default)
    {
        if (!IsPipelineConfigured)
        {
            _logger.LogWarning("Azure ingestion pipeline chua du cau hinh (Search/Storage/OpenAI).");
            return;
        }

        var indexerClient = CreateIndexerClient();
        await EnsureDataSourceAsync(indexerClient, cancellationToken);
        await EnsureSkillsetViaRestAsync(cancellationToken);
        await EnsureIndexerAsync(indexerClient, cancellationToken);
        _logger.LogInformation("Azure ingestion pipeline (datasource/skillset/indexer) san sang.");
    }

    public async Task<IndexerTriggerResult> TryRunIndexerAsync(CancellationToken cancellationToken = default)
    {
        if (!_searchOptions.Enabled)
            return new IndexerTriggerResult(IndexerTriggerOutcome.Started, "Azure Search chua bat.");

        var indexerClient = CreateIndexerClient();
        try
        {
            await indexerClient.RunIndexerAsync(_searchOptions.IndexerName, cancellationToken);
            _logger.LogInformation("Da trigger indexer {Indexer}.", _searchOptions.IndexerName);
            return new IndexerTriggerResult(IndexerTriggerOutcome.Started);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _logger.LogInformation(
                "Indexer {Indexer} dang chay (409 conflict); chuyen sang poll status.",
                _searchOptions.IndexerName);
            return new IndexerTriggerResult(
                IndexerTriggerOutcome.AlreadyRunning,
                "Indexer dang chay tu truoc; poll status thay vi fail.");
        }
    }

    public async Task<IndexerExecutionSnapshot> GetExecutionSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!_searchOptions.Enabled)
            return new IndexerExecutionSnapshot(false, null, null, []);

        var indexerClient = CreateIndexerClient();
        var status = await indexerClient.GetIndexerStatusAsync(_searchOptions.IndexerName, cancellationToken);
        var lastResult = status.Value.LastResult;
        var issues = BuildIndexerIssues(lastResult);
        var lastRunStatus = lastResult?.Status;
        return new IndexerExecutionSnapshot(
            lastRunStatus == IndexerExecutionStatus.InProgress,
            lastRunStatus,
            lastResult?.ErrorMessage,
            issues);
    }

    private static IReadOnlyList<IndexerExecutionIssue> BuildIndexerIssues(IndexerExecutionResult? lastResult)
    {
        if (lastResult is null)
            return [];

        var issues = new List<IndexerExecutionIssue>();
        if (lastResult.Errors is not null)
        {
            foreach (var error in lastResult.Errors)
            {
                issues.Add(new IndexerExecutionIssue(
                    error.Key ?? string.Empty,
                    error.ErrorMessage ?? error.Name ?? "Indexer item error",
                    IsError: true));
            }
        }

        if (lastResult.Warnings is not null)
        {
            foreach (var warning in lastResult.Warnings)
            {
                issues.Add(new IndexerExecutionIssue(
                    warning.Key ?? string.Empty,
                    warning.Message ?? warning.Name ?? "Indexer item warning",
                    IsError: false));
            }
        }

        return issues;
    }

    private async Task EnsureDataSourceAsync(SearchIndexerClient client, CancellationToken cancellationToken)
    {
        var dataSource = new SearchIndexerDataSourceConnection(
            _searchOptions.DataSourceName,
            SearchIndexerDataSourceType.AzureBlob,
            _storageOptions.ConnectionString!,
            new SearchIndexerDataContainer(_storageOptions.ContainerName))
        {
            Description = "Knowledge PDF/text blobs for chunk-level RAG indexing."
        };

        await client.CreateOrUpdateDataSourceConnectionAsync(dataSource, cancellationToken: cancellationToken);
    }

    private async Task EnsureSkillsetViaRestAsync(CancellationToken cancellationToken)
    {
        // REST required: SDK 12.x skillset models do not expose indexProjections yet.
        var payload = AzureSearchSkillsetBuilder.BuildJson(
            AzureSearchSkillsetBuilder.FromConfig(_searchOptions, _openAiOptions));

        var client = _httpClientFactory.CreateClient(nameof(AzureSearchIngestionService));
        var uri = new Uri(
            $"{_searchOptions.Endpoint!.TrimEnd('/')}/skillsets/{_searchOptions.SkillsetName}?api-version={_searchOptions.AdminApiVersion}");
        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("api-key", _searchOptions.ApiKey);

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Tao skillset {_searchOptions.SkillsetName} that bai ({(int)response.StatusCode}): {body}");
        }
    }

    private async Task EnsureIndexerAsync(SearchIndexerClient client, CancellationToken cancellationToken)
    {
        var indexer = new SearchIndexer(
            _searchOptions.IndexerName,
            _searchOptions.DataSourceName,
            _searchOptions.IndexName)
        {
            Description = "Index knowledge PDF/text blobs into chunk-level search index.",
            SkillsetName = _searchOptions.SkillsetName,
            Schedule = new IndexingSchedule(TimeSpan.FromMinutes(5)),
            Parameters = new IndexingParameters
            {
                Configuration =
                {
                    ["dataToExtract"] = "contentAndMetadata",
                    ["parsingMode"] = "default"
                }
            }
        };

        await client.CreateOrUpdateIndexerAsync(indexer, cancellationToken: cancellationToken);
    }

    private SearchIndexerClient CreateIndexerClient() =>
        new(new Uri(_searchOptions.Endpoint!), new AzureKeyCredential(_searchOptions.ApiKey!));
}
