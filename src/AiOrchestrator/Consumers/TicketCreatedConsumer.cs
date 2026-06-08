using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;

namespace SupportPoc.AiOrchestrator.Consumers;

/// <summary>
/// Proposal pipeline: AI-only trong orchestrator, sau do mot lenh DB-only ConsiderAutoSuggestion.
/// </summary>
public sealed class TicketCreatedConsumer : IConsumer<ITicketCreated>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OrchestratorDbContext _db;
    private readonly AiPipelineService _pipeline;
    private readonly IConsiderAutoSuggestionGateway _consider;
    private readonly AutoSuggestionOptions _options;
    private readonly ILogger<TicketCreatedConsumer> _logger;

    public TicketCreatedConsumer(
        OrchestratorDbContext db,
        AiPipelineService pipeline,
        IConsiderAutoSuggestionGateway consider,
        IOptions<AutoSuggestionOptions> options,
        ILogger<TicketCreatedConsumer> logger)
    {
        _db = db;
        _pipeline = pipeline;
        _consider = consider;
        _options = options.Value;
        _logger = logger;
    }
    public async Task Consume(ConsumeContext<ITicketCreated> context)
    {
        var msg = context.Message;

        if (msg.Question.Has(FaultInjection.ForcePoisonAi))
        {
            _logger.LogError("FaultInjection: ForcePoisonAi -> poison for DLQ test.");
            throw new InvalidOperationException("Simulated poison message (fault injection).");
        }

        var job = await _db.AutoSuggestionJobs.FindAsync([msg.JobId], context.CancellationToken);
        if (job is null)
        {
            job = new AutoSuggestionJob
            {
                JobId = msg.JobId,
                TicketId = msg.TicketId,
                EmployeeId = msg.EmployeeId,
                Question = msg.Question,
                Category = msg.Category,
                Status = AutoSuggestionJobStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _db.AutoSuggestionJobs.Add(job);
            await _db.SaveChangesAsync(context.CancellationToken);
        }
        else if (job.Status is AutoSuggestionJobStatus.Completed or AutoSuggestionJobStatus.Discarded or AutoSuggestionJobStatus.Failed)
        {
            _logger.LogInformation(
                "TicketCreated duplicate for terminal job JobId={JobId} Status={Status}",
                msg.JobId,
                job.Status);
            return;
        }
        else if (job.Status == AutoSuggestionJobStatus.Running)
        {
            job.Status = AutoSuggestionJobStatus.Running;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(context.CancellationToken);
        }

        try
        {
            var result = job.Status == AutoSuggestionJobStatus.Produced
                ? RestoreProducedResult(job)
                : await ProduceAsync(job, msg, context.CancellationToken);

            if (job.Status != AutoSuggestionJobStatus.Produced)
                throw new InvalidOperationException($"Auto suggestion job is not ready to consider. Status={job.Status}");

            if (msg.Question.Has(FaultInjection.ForceSkipConsider))
            {
                _logger.LogWarning(
                    "FaultInjection: ForceSkipConsider — job Produced, skip consider. JobId={JobId}",
                    msg.JobId);
                return;
            }

            var considerTimeout = TimeSpan.FromSeconds(Math.Max(5, _options.ConsiderRequestTimeoutSeconds));
            var consider = await _consider.SendAsync(
                new ConsiderAutoSuggestion(
                    msg.JobId,
                    msg.TicketId,
                    result.Category,
                    result.Suggestion,
                    result.Related),
                considerTimeout,
                context.CancellationToken);

            if (!consider.Accepted)
            {
                job.Status = AutoSuggestionJobStatus.Discarded;
                job.DiscardReason = consider.RejectReason;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(context.CancellationToken);
                return;
            }
            job.Status = AutoSuggestionJobStatus.Completed;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(context.CancellationToken);

            await context.Publish<IAiSuggestionGenerated>(new AiSuggestionGenerated(msg.JobId, msg.TicketId));
        }
        catch (RequestTimeoutException ex)
        {
            await FailJobAsync(job, $"Consider request timed out: {ex.Message}", context);
        }
        catch (TaskCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
        {
            await FailJobAsync(job, $"Consider request timed out: {ex.Message}", context);
        }        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await FailJobAsync(job, ex.Message, context);
        }
    }

    private async Task FailJobAsync(AutoSuggestionJob job, string reason, ConsumeContext context)
    {
        _logger.LogError("Auto suggestion job failed JobId={JobId} TicketId={TicketId} Reason={Reason}",
            job.JobId, job.TicketId, reason);

        job.Status = AutoSuggestionJobStatus.Failed;
        job.FailureReason = reason;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(context.CancellationToken);

        await context.Publish<IAutoSuggestionFailed>(new AutoSuggestionFailed(job.JobId, job.TicketId, reason));
    }

    private async Task<AiPipelineService.PipelineResult> ProduceAsync(
        AutoSuggestionJob job,
        ITicketCreated msg,
        CancellationToken cancellationToken)
    {
        var result = await _pipeline.RunAsync(
            msg.Question,
            msg.Category,
            cancellationToken);

        job.ProducedCategory = result.Category;
        job.ProducedSuggestion = result.Suggestion;
        job.ProducedRelatedDocumentsJson = JsonSerializer.Serialize(result.Related, JsonOptions);
        job.Status = AutoSuggestionJobStatus.Produced;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private static AiPipelineService.PipelineResult RestoreProducedResult(AutoSuggestionJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ProducedCategory))
            throw new InvalidOperationException("Produced job is missing category.");
        if (string.IsNullOrWhiteSpace(job.ProducedSuggestion))
            throw new InvalidOperationException("Produced job is missing suggestion.");

        IReadOnlyList<RelatedDocument> related;
        try
        {
            related = JsonSerializer.Deserialize<IReadOnlyList<RelatedDocument>>(
                job.ProducedRelatedDocumentsJson,
                JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Produced job has invalid related documents JSON.", ex);
        }

        return new AiPipelineService.PipelineResult(job.ProducedCategory, job.ProducedSuggestion, related);
    }
}

public sealed class TicketCreatedConsumerDefinition : ConsumerDefinition<TicketCreatedConsumer>
{
    public TicketCreatedConsumerDefinition()
    {
        EndpointName = "auto-suggestion-ticket-created";
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<TicketCreatedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 2000, 5000, 10000));
        endpointConfigurator.UseEntityFrameworkOutbox<OrchestratorDbContext>(context);

        if (endpointConfigurator is IServiceBusReceiveEndpointConfigurator sb)
        {
            sb.LockDuration = TimeSpan.FromMinutes(5);
            sb.MaxAutoRenewDuration = TimeSpan.FromMinutes(15);
            sb.MaxDeliveryCount = 5;
            sb.ConfigureDeadLetterQueueErrorTransport();
            sb.ConfigureDeadLetterQueueDeadLetterTransport();
        }
    }
}
