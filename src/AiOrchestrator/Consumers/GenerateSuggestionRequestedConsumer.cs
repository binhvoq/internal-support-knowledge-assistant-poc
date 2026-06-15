using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Testing;

namespace SupportPoc.AiOrchestrator.Consumers;

/// <summary>Claim/replay durable AI job — khong goi pipeline; worker xu ly LLM/HTTP ngoai consumer transaction.</summary>
public sealed class GenerateSuggestionRequestedConsumer : IConsumer<IGenerateSuggestionRequested>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OrchestratorDbContext _db;
    private readonly IAiGenerationAttemptLifecycle _attemptLifecycle;
    private readonly ILogger<GenerateSuggestionRequestedConsumer> _logger;

    public GenerateSuggestionRequestedConsumer(
        OrchestratorDbContext db,
        IAiGenerationAttemptLifecycle attemptLifecycle,
        ILogger<GenerateSuggestionRequestedConsumer> logger)
    {
        _db = db;
        _attemptLifecycle = attemptLifecycle;
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

        if (!await TryEnqueueAttemptAsync(msg, context.CancellationToken))
        {
            existing = await _db.AiGenerationAttempts
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.AttemptId == msg.AttemptId, context.CancellationToken);
            if (existing is not null)
                await ReplayOrDeferStoredOutcomeAsync(context, msg, existing);
            return;
        }

        _logger.LogInformation(
            "AI attempt enqueued for background worker SagaId={SagaId} AttemptId={AttemptId}",
            msg.SagaId,
            msg.AttemptId);
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

        if (existing.Status == AiGenerationAttemptStatus.Superseded)
        {
            _logger.LogInformation(
                "AI attempt superseded — duplicate delivery ignored SagaId={SagaId} AttemptId={AttemptId}",
                msg.SagaId,
                msg.AttemptId);
            return;
        }

        _logger.LogInformation(
            "AI attempt already pending/running SagaId={SagaId} AttemptId={AttemptId} Status={Status}; duplicate delivery ignored.",
            msg.SagaId,
            msg.AttemptId,
            existing.Status);
    }

    private async Task<bool> TryEnqueueAttemptAsync(IGenerateSuggestionRequested msg, CancellationToken cancellationToken)
    {
        if (await _attemptLifecycle.HasActiveAttemptForJobAsync(
                msg.JobId,
                excludingAttemptId: msg.AttemptId,
                cancellationToken))
        {
            _logger.LogWarning(
                "AI attempt enqueue blocked — active attempt exists for JobId={JobId} AttemptId={AttemptId}",
                msg.JobId,
                msg.AttemptId);
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        _db.AiGenerationAttempts.Add(new AiGenerationAttemptEntity
        {
            AttemptId = msg.AttemptId,
            SagaId = msg.SagaId,
            JobId = msg.JobId,
            TicketId = msg.TicketId,
            Question = msg.Question,
            RequestedCategory = msg.Category,
            Status = AiGenerationAttemptStatus.Pending,
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
                "AI attempt enqueue race AttemptId={AttemptId} — reload stored outcome if available.",
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
