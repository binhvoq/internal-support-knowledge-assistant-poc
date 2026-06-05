using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.KnowledgeService.Data;
using SupportPoc.KnowledgeService.Options;
using SupportPoc.KnowledgeService.Search;

namespace SupportPoc.KnowledgeService.Services;

/// <summary>Periodically refreshes documents stuck in Processing after the initial upload poll window.</summary>
public sealed class DocumentIngestionRefreshBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly AzureSearchOptions _searchOptions;
    private readonly ILogger<DocumentIngestionRefreshBackgroundService> _logger;

    public DocumentIngestionRefreshBackgroundService(
        IServiceProvider services,
        IOptions<AzureSearchOptions> searchOptions,
        ILogger<DocumentIngestionRefreshBackgroundService> logger)
    {
        _services = services;
        _searchOptions = searchOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_searchOptions.Enabled)
        {
            _logger.LogInformation("Bo qua background ingestion refresh vi Azure Search chua bat.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _searchOptions.IngestionRefreshIntervalSeconds));
        _logger.LogInformation("Background ingestion refresh chay moi {Seconds}s.", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshProcessingDocumentsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Background ingestion refresh gap.");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RefreshProcessingDocumentsAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
        var ingestion = scope.ServiceProvider.GetRequiredService<AzureSearchIngestionService>();
        if (!ingestion.IsPipelineConfigured)
            return;

        var refresher = scope.ServiceProvider.GetRequiredService<DocumentIngestionStatusRefresher>();
        var processingCandidates = await db.Documents
            .Where(d => d.IngestionStatus == "Processing")
            .ToListAsync(cancellationToken);
        var processing = processingCandidates
            .OrderBy(d => d.UpdatedAt)
            .Take(20)
            .ToList();

        if (processing.Count == 0)
            return;

        await ingestion.TryRunIndexerAsync(cancellationToken);

        foreach (var entity in processing)
        {
            var decision = await refresher.EvaluateDocumentAsync(entity, pollTimedOut: true, cancellationToken);
            if (decision.Action is IngestionPollAction.Continue)
                continue;

            var previousStatus = entity.IngestionStatus;
            DocumentIngestionStatusRefresher.ApplyDecision(entity, decision, _searchOptions.IndexName);
            if (!string.Equals(previousStatus, entity.IngestionStatus, StringComparison.Ordinal)
                || decision.Action == IngestionPollAction.Ready)
            {
                _logger.LogInformation(
                    "Background refresh cap nhat {DocumentId}: {Status} - {Message}",
                    entity.Id,
                    entity.IngestionStatus,
                    entity.IngestionMessage);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
