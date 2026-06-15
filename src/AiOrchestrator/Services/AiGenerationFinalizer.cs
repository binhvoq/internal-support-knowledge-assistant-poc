using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;

namespace SupportPoc.AiOrchestrator.Services;

public enum AiGenerationFinalizeOutcome
{
    Applied,
    NotFound,
    SkippedTerminal,
    SkippedLeaseMismatch,
    SkippedStaleWrite
}

public sealed class AiGenerationFinalizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OrchestratorDbContext _db;
    private readonly IPublishEndpoint _publish;
    private readonly AutoSuggestionOptions _options;
    private readonly ILogger<AiGenerationFinalizer> _logger;

    public AiGenerationFinalizer(
        OrchestratorDbContext db,
        IPublishEndpoint publish,
        IOptions<AutoSuggestionOptions> options,
        ILogger<AiGenerationFinalizer> logger)
    {
        _db = db;
        _publish = publish;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiGenerationFinalizeOutcome> FinalizeSuccessAsync(
        Guid attemptId,
        string leaseOwner,
        AiPipelineService.PipelineResult result,
        CancellationToken cancellationToken)
    {
        var attempt = await _db.AiGenerationAttempts
            .FirstOrDefaultAsync(x => x.AttemptId == attemptId, cancellationToken);
        if (attempt is null)
            return AiGenerationFinalizeOutcome.NotFound;

        var preCheck = ValidateLeaseForFinalize(attempt, leaseOwner, attemptId, "success");
        if (preCheck is not null)
            return preCheck.Value;

        var now = DateTimeOffset.UtcNow;
        attempt.Status = AiGenerationAttemptStatus.Completed;
        attempt.Category = result.Category;
        attempt.Suggestion = result.Suggestion;
        attempt.RelatedDocumentsJson = JsonSerializer.Serialize(result.Related, JsonOptions);
        attempt.Error = null;
        attempt.LeaseOwner = null;
        attempt.LeaseUntil = null;
        attempt.CompletedAt = now;
        attempt.UpdatedAt = now;

        return await CommitWithPublishAsync(
            attempt,
            () => _publish.Publish<ISuggestionGenerated>(new SuggestionGenerated(
                attempt.SagaId,
                attempt.AttemptId,
                attempt.JobId,
                attempt.TicketId,
                result.Category,
                result.Suggestion,
                result.Related), cancellationToken),
            cancellationToken);
    }

    public async Task<AiGenerationFinalizeOutcome> FinalizeFailureAsync(
        Guid attemptId,
        string leaseOwner,
        string reason,
        CancellationToken cancellationToken)
    {
        var attempt = await _db.AiGenerationAttempts
            .FirstOrDefaultAsync(x => x.AttemptId == attemptId, cancellationToken);
        if (attempt is null)
            return AiGenerationFinalizeOutcome.NotFound;

        var preCheck = ValidateLeaseForFinalize(attempt, leaseOwner, attemptId, "failure");
        if (preCheck is not null)
            return preCheck.Value;

        var now = DateTimeOffset.UtcNow;
        attempt.Error = reason;
        attempt.LeaseOwner = null;
        attempt.LeaseUntil = null;
        attempt.UpdatedAt = now;

        if (attempt.RetryCount < _options.MaxGenerationRetries)
        {
            attempt.RetryCount++;
            attempt.Status = AiGenerationAttemptStatus.Pending;
            attempt.NextRunAt = now + ComputeBackoff(attempt.RetryCount);
            _logger.LogWarning(
                "AI generation scheduled retry AttemptId={AttemptId} RetryCount={RetryCount} NextRunAt={NextRunAt}",
                attemptId,
                attempt.RetryCount,
                attempt.NextRunAt);
            return await CommitAsync(attempt, cancellationToken);
        }

        attempt.Status = AiGenerationAttemptStatus.Failed;
        attempt.CompletedAt = now;

        return await CommitWithPublishAsync(
            attempt,
            () => _publish.Publish<ISuggestionGenerationFailed>(new SuggestionGenerationFailed(
                attempt.SagaId,
                attempt.AttemptId,
                attempt.JobId,
                attempt.TicketId,
                reason), cancellationToken),
            cancellationToken);
    }

    private AiGenerationFinalizeOutcome? ValidateLeaseForFinalize(
        AiGenerationAttemptEntity attempt,
        string leaseOwner,
        Guid attemptId,
        string operation)
    {
        if (IsTerminal(attempt.Status))
        {
            _logger.LogInformation(
                "AI finalize {Operation} skipped — attempt already terminal AttemptId={AttemptId} Status={Status}",
                operation,
                attemptId,
                attempt.Status);
            return AiGenerationFinalizeOutcome.SkippedTerminal;
        }

        if (!OwnsLease(attempt, leaseOwner))
        {
            _logger.LogInformation(
                "Stale finalize {Operation} skipped AttemptId={AttemptId} ExpectedLeaseOwner={ExpectedLeaseOwner} ActualLeaseOwner={ActualLeaseOwner} Status={Status}",
                operation,
                attemptId,
                leaseOwner,
                attempt.LeaseOwner,
                attempt.Status);
            return AiGenerationFinalizeOutcome.SkippedLeaseMismatch;
        }

        return null;
    }

    private async Task<AiGenerationFinalizeOutcome> CommitWithPublishAsync(
        AiGenerationAttemptEntity attempt,
        Func<Task> publish,
        CancellationToken cancellationToken)
    {
        try
        {
            await publish();
            await _db.SaveChangesAsync(cancellationToken);
            return AiGenerationFinalizeOutcome.Applied;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            return HandleStaleWrite(attempt.AttemptId, ex);
        }
    }

    private async Task<AiGenerationFinalizeOutcome> CommitAsync(
        AiGenerationAttemptEntity attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return AiGenerationFinalizeOutcome.Applied;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            return HandleStaleWrite(attempt.AttemptId, ex);
        }
    }

    private AiGenerationFinalizeOutcome HandleStaleWrite(Guid attemptId, DbUpdateConcurrencyException ex)
    {
        _logger.LogInformation(
            ex,
            "Stale finalize write skipped — row changed after read AttemptId={AttemptId}",
            attemptId);
        _db.ChangeTracker.Clear();
        return AiGenerationFinalizeOutcome.SkippedStaleWrite;
    }

    private static bool IsTerminal(string status) =>
        AiGenerationAttemptStatuses.IsTerminal(status);

    private static bool OwnsLease(AiGenerationAttemptEntity attempt, string leaseOwner) =>
        attempt.Status == AiGenerationAttemptStatus.Running
        && string.Equals(attempt.LeaseOwner, leaseOwner, StringComparison.Ordinal);

    private static TimeSpan ComputeBackoff(int retryCount) =>
        TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, retryCount) * 5));
}
