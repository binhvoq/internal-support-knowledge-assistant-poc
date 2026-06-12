using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;

namespace SupportPoc.AiOrchestrator.Consumers;

/// <summary>AI worker — chi chay pipeline va reply ve saga, khong goi TicketService.</summary>
public sealed class GenerateSuggestionRequestedConsumer : IConsumer<IGenerateSuggestionRequested>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAiPipelineService _pipeline;
    private readonly OrchestratorDbContext _db;
    private readonly ILogger<GenerateSuggestionRequestedConsumer> _logger;

    public GenerateSuggestionRequestedConsumer(
        IAiPipelineService pipeline,
        OrchestratorDbContext db,
        ILogger<GenerateSuggestionRequestedConsumer> logger)
    {
        _pipeline = pipeline;
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<IGenerateSuggestionRequested> context)
    {
        var msg = context.Message;

        if (msg.Question.Has(FaultInjection.ForcePoisonAi))
        {
            _logger.LogError("FaultInjection: ForcePoisonAi -> poison for DLQ test.");
            throw new InvalidOperationException("Simulated poison message (fault injection).");
        }

        if (msg.Question.Has(FaultInjection.ForceSkipConsider) || msg.Question.Has(FaultInjection.ForceSkipGenerate))
        {
            _logger.LogWarning(
                "FaultInjection: skip generate SagaId={SagaId} AttemptId={AttemptId}",
                msg.SagaId,
                msg.AttemptId);
            return;
        }

        var existing = await _db.AiGenerationAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AttemptId == msg.AttemptId, context.CancellationToken);

        if (existing is not null)
        {
            await ReplayOrDeferStoredOutcomeAsync(context, msg, existing);
            return;
        }

        if (!await TryClaimAttemptAsync(msg, context.CancellationToken))
        {
            existing = await _db.AiGenerationAttempts
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.AttemptId == msg.AttemptId, context.CancellationToken);
            if (existing is not null)
            {
                await ReplayOrDeferStoredOutcomeAsync(context, msg, existing);
                return;
            }
        }

        var attempt = await _db.AiGenerationAttempts
            .FirstAsync(x => x.AttemptId == msg.AttemptId, context.CancellationToken);

        try
        {
            var result = await _pipeline.RunAsync(msg.Question, msg.Category, context.CancellationToken);
            var now = DateTimeOffset.UtcNow;
            attempt.Status = AiGenerationAttemptStatus.Completed;
            attempt.Category = result.Category;
            attempt.Suggestion = result.Suggestion;
            attempt.RelatedDocumentsJson = JsonSerializer.Serialize(result.Related, JsonOptions);
            attempt.Error = null;
            attempt.CompletedAt = now;
            attempt.UpdatedAt = now;
            await _db.SaveChangesAsync(context.CancellationToken);

            await PublishGeneratedAsync(context, msg, result.Category, result.Suggestion, result.Related);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Suggestion generation failed SagaId={SagaId} AttemptId={AttemptId}",
                msg.SagaId,
                msg.AttemptId);

            var now = DateTimeOffset.UtcNow;
            attempt.Status = AiGenerationAttemptStatus.Failed;
            attempt.Error = ex.Message;
            attempt.CompletedAt = now;
            attempt.UpdatedAt = now;
            await _db.SaveChangesAsync(context.CancellationToken);

            await PublishFailedAsync(context, msg, ex.Message);
        }
    }

    private async Task ReplayOrDeferStoredOutcomeAsync(
        ConsumeContext<IGenerateSuggestionRequested> context,
        IGenerateSuggestionRequested msg,
        AiGenerationAttemptEntity existing)
    {
        if (existing.Status == AiGenerationAttemptStatus.Completed)
        {
            _logger.LogInformation(
                "AI attempt idempotent replay SagaId={SagaId} AttemptId={AttemptId}",
                msg.SagaId,
                msg.AttemptId);
            var related = DeserializeRelated(existing.RelatedDocumentsJson);
            await PublishGeneratedAsync(
                context,
                msg,
                existing.Category ?? msg.Category,
                existing.Suggestion ?? string.Empty,
                related);
            return;
        }

        if (existing.Status == AiGenerationAttemptStatus.Failed)
        {
            _logger.LogInformation(
                "AI attempt idempotent failure replay SagaId={SagaId} AttemptId={AttemptId}",
                msg.SagaId,
                msg.AttemptId);
            await PublishFailedAsync(context, msg, existing.Error ?? "Previous attempt failed.");
            return;
        }

        _logger.LogInformation(
            "AI attempt already running SagaId={SagaId} AttemptId={AttemptId}; duplicate delivery deferred.",
            msg.SagaId,
            msg.AttemptId);
        throw new InvalidOperationException($"AI generation attempt {msg.AttemptId} is already running.");
    }

    private async Task<bool> TryClaimAttemptAsync(IGenerateSuggestionRequested msg, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = msg.AttemptId,
            SagaId = msg.SagaId,
            JobId = msg.JobId,
            TicketId = msg.TicketId,
            Status = AiGenerationAttemptStatus.Running,
            RelatedDocumentsJson = "[]",
            StartedAt = now,
            UpdatedAt = now
        });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(
                ex,
                "AI attempt claim race AttemptId={AttemptId} — reload stored outcome if available.",
                msg.AttemptId);
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    private static IReadOnlyList<RelatedDocument> DeserializeRelated(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<RelatedDocument>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static Task PublishGeneratedAsync(
        ConsumeContext context,
        IGenerateSuggestionRequested msg,
        string category,
        string suggestion,
        IReadOnlyList<RelatedDocument> related) =>
        context.Publish<ISuggestionGenerated>(new SuggestionGenerated(
            msg.SagaId,
            msg.AttemptId,
            msg.JobId,
            msg.TicketId,
            category,
            suggestion,
            related));

    private static Task PublishFailedAsync(
        ConsumeContext context,
        IGenerateSuggestionRequested msg,
        string reason) =>
        context.Publish<ISuggestionGenerationFailed>(new SuggestionGenerationFailed(
            msg.SagaId,
            msg.AttemptId,
            msg.JobId,
            msg.TicketId,
            reason));
}

public sealed class GenerateSuggestionRequestedConsumerDefinition
    : ConsumerDefinition<GenerateSuggestionRequestedConsumer>
{
    public GenerateSuggestionRequestedConsumerDefinition()
    {
        EndpointName = "generate-suggestion-requested";
        ConcurrentMessageLimit = 4;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<GenerateSuggestionRequestedConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(500, 2000, 5000, 10000));
        // Consumer outbox: Program.cs AddEntityFrameworkConsumerOutbox<OrchestratorDbContext>()

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
