using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;

namespace SupportPoc.AiOrchestrator.Services;

public sealed class AiGenerationWorkerService(
    IServiceProvider serviceProvider,
    IOptions<AutoSuggestionOptions> options,
    ILogger<AiGenerationWorkerService> logger) : BackgroundService
{
    private const int MaxClaimCandidates = 5;

    private readonly string _leaseOwner = $"{Environment.MachineName}:{Guid.NewGuid():N}"[..64];
    private SemaphoreSlim? _concurrency;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        var concurrency = Math.Max(1, opts.AiGenerationWorkerConcurrency);
        _concurrency = new SemaphoreSlim(concurrency, concurrency);
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, opts.AiGenerationWorkerPollIntervalSeconds));

        logger.LogInformation(
            "AI generation worker started LeaseOwner={LeaseOwner} Concurrency={Concurrency}",
            _leaseOwner,
            concurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAvailableJobsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AI generation worker iteration failed");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }
    }

    internal async Task ProcessAvailableJobsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_concurrency is null || !await _concurrency.WaitAsync(0, cancellationToken))
                return;

            AiGenerationAttemptEntity? claimed;
            await using (var claimScope = serviceProvider.CreateAsyncScope())
            {
                var db = claimScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                claimed = await TryClaimNextAttemptAsync(db, cancellationToken);
            }

            if (claimed is null)
            {
                _concurrency.Release();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessClaimedAttemptAsync(claimed, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(
                        ex,
                        "AI generation job failed AttemptId={AttemptId}",
                        claimed.AttemptId);
                    await using var scope = serviceProvider.CreateAsyncScope();
                    var finalizer = scope.ServiceProvider.GetRequiredService<AiGenerationFinalizer>();
                    await finalizer.FinalizeFailureAsync(
                        claimed.AttemptId,
                        _leaseOwner,
                        ex.Message,
                        cancellationToken);
                }
                finally
                {
                    _concurrency?.Release();
                }
            }, cancellationToken);
        }
    }

    internal static async Task<AiGenerationAttemptEntity?> TryClaimNextAttemptAsync(
        OrchestratorDbContext db,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var candidateRows = (await db.AiGenerationAttempts
            .AsNoTracking()
            .Where(x => x.Status == AiGenerationAttemptStatus.Pending
                     || x.Status == AiGenerationAttemptStatus.Running)
            .Take(50)
            .ToListAsync(cancellationToken))
            .Where(row => IsClaimable(row, now))
            .OrderBy(row => row.StartedAt)
            .Take(MaxClaimCandidates);

        foreach (var row in candidateRows)
        {
            if (await TryConditionalClaimAsync(db, row, leaseOwner, leaseDuration, now, cancellationToken))
            {
                return await db.AiGenerationAttempts
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.AttemptId == row.AttemptId, cancellationToken);
            }
        }

        return null;
    }

    internal static bool IsClaimable(AiGenerationAttemptEntity attempt, DateTimeOffset now) =>
        (attempt.Status == AiGenerationAttemptStatus.Pending
         && (attempt.NextRunAt is null || attempt.NextRunAt <= now))
        || (attempt.Status == AiGenerationAttemptStatus.Running
            && attempt.LeaseUntil is not null
            && attempt.LeaseUntil < now);

    private static async Task<bool> TryConditionalClaimAsync(
        OrchestratorDbContext db,
        AiGenerationAttemptEntity candidate,
        string leaseOwner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var leaseUntil = now + leaseDuration;
        var claimSetters = BuildClaimSetters(leaseOwner, leaseUntil, now);
        var attemptId = candidate.AttemptId;

        if (candidate.Status == AiGenerationAttemptStatus.Pending && candidate.NextRunAt is null)
        {
            return await db.AiGenerationAttempts
                .Where(x => x.AttemptId == attemptId
                    && x.Status == AiGenerationAttemptStatus.Pending
                    && x.NextRunAt == null)
                .ExecuteUpdateAsync(claimSetters, cancellationToken) == 1;
        }

        if (candidate.Status == AiGenerationAttemptStatus.Pending
            && candidate.NextRunAt is not null
            && candidate.NextRunAt <= now)
        {
            var nextRunAt = candidate.NextRunAt.Value;
            return await db.AiGenerationAttempts
                .Where(x => x.AttemptId == attemptId
                    && x.Status == AiGenerationAttemptStatus.Pending
                    && x.NextRunAt == nextRunAt)
                .ExecuteUpdateAsync(claimSetters, cancellationToken) == 1;
        }

        if (candidate.Status == AiGenerationAttemptStatus.Running
            && candidate.LeaseUntil is not null
            && candidate.LeaseUntil < now)
        {
            var observedLeaseUntil = candidate.LeaseUntil.Value;
            return await db.AiGenerationAttempts
                .Where(x => x.AttemptId == attemptId
                    && x.Status == AiGenerationAttemptStatus.Running
                    && x.LeaseUntil == observedLeaseUntil)
                .ExecuteUpdateAsync(claimSetters, cancellationToken) == 1;
        }

        return false;
    }

    private static Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<AiGenerationAttemptEntity>> BuildClaimSetters(
        string leaseOwner,
        DateTimeOffset leaseUntil,
        DateTimeOffset now) =>
        setters => setters
            .SetProperty(x => x.Status, AiGenerationAttemptStatus.Running)
            .SetProperty(x => x.LeaseOwner, leaseOwner)
            .SetProperty(x => x.LeaseUntil, leaseUntil)
            .SetProperty(x => x.UpdatedAt, now);

    internal static async Task<bool> TryRenewLeaseAsync(
        OrchestratorDbContext db,
        Guid attemptId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now + leaseDuration;
        var affected = await db.AiGenerationAttempts
            .Where(x => x.AttemptId == attemptId
                && x.Status == AiGenerationAttemptStatus.Running
                && x.LeaseOwner == leaseOwner)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LeaseUntil, leaseUntil)
                .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        return affected == 1;
    }

    private async Task<AiGenerationAttemptEntity?> TryClaimNextAttemptAsync(
        OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
        var leaseDuration = GetLeaseDuration();
        return await TryClaimNextAttemptAsync(db, _leaseOwner, leaseDuration, cancellationToken);
    }

    private TimeSpan GetLeaseDuration() =>
        TimeSpan.FromSeconds(Math.Max(30, options.Value.AiGenerationLeaseSeconds));

    private static TimeSpan GetRenewalInterval(TimeSpan leaseDuration) =>
        TimeSpan.FromSeconds(Math.Max(30, leaseDuration.TotalSeconds / 3));

    private async Task ProcessClaimedAttemptAsync(
        AiGenerationAttemptEntity claimed,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(claimed.Question))
        {
            await using var failScope = serviceProvider.CreateAsyncScope();
            var failFinalizer = failScope.ServiceProvider.GetRequiredService<AiGenerationFinalizer>();
            await failFinalizer.FinalizeFailureAsync(
                claimed.AttemptId,
                _leaseOwner,
                "Missing question payload for durable AI job.",
                cancellationToken);
            return;
        }

        var leaseDuration = GetLeaseDuration();
        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = RunLeaseRenewalLoopAsync(
            claimed.AttemptId,
            _leaseOwner,
            leaseDuration,
            pipelineCts,
            cancellationToken);

        AiPipelineService.PipelineResult result;
        try
        {
            await using var pipelineScope = serviceProvider.CreateAsyncScope();
            var pipeline = pipelineScope.ServiceProvider.GetRequiredService<IAiPipelineService>();
            result = await pipeline.RunAsync(
                claimed.Question,
                claimed.RequestedCategory,
                pipelineCts.Token);
        }
        catch (OperationCanceledException) when (pipelineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "AI generation pipeline cancelled after lease loss AttemptId={AttemptId}",
                claimed.AttemptId);
            return;
        }
        finally
        {
            await pipelineCts.CancelAsync();
            try
            {
                await renewalTask;
            }
            catch (OperationCanceledException)
            {
                // expected when stopping renewal loop
            }
        }

        await using var finalizeScope = serviceProvider.CreateAsyncScope();
        var finalizer = finalizeScope.ServiceProvider.GetRequiredService<AiGenerationFinalizer>();
        var outcome = await finalizer.FinalizeSuccessAsync(
            claimed.AttemptId,
            _leaseOwner,
            result,
            cancellationToken);

        if (outcome == AiGenerationFinalizeOutcome.Applied)
        {
            logger.LogInformation(
                "AI generation completed AttemptId={AttemptId} SagaId={SagaId}",
                claimed.AttemptId,
                claimed.SagaId);
        }
    }

    private async Task RunLeaseRenewalLoopAsync(
        Guid attemptId,
        string leaseOwner,
        TimeSpan leaseDuration,
        CancellationTokenSource pipelineCts,
        CancellationToken stoppingToken)
    {
        var interval = GetRenewalInterval(leaseDuration);

        while (!stoppingToken.IsCancellationRequested && !pipelineCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (pipelineCts.IsCancellationRequested)
                break;

            await using var scope = serviceProvider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var renewed = await TryRenewLeaseAsync(db, attemptId, leaseOwner, leaseDuration, stoppingToken);
            if (renewed)
                continue;

            logger.LogWarning(
                "AI generation lease lost AttemptId={AttemptId} LeaseOwner={LeaseOwner}",
                attemptId,
                leaseOwner);
            await pipelineCts.CancelAsync();
            break;
        }
    }
}
