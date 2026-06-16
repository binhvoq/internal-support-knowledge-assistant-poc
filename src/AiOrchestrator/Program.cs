using System.Security.Claims;
using Azure.Messaging.ServiceBus.Administration;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using SupportPoc.AiOrchestrator.Consumers;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Mcp;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Auth;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Data;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;
using SupportPoc.Shared.Options;
using SupportPoc.Shared.Telemetry;
var builder = WebApplication.CreateBuilder(args);
ProductionSecurityGuard.Validate(builder.Environment, builder.Configuration);
builder.Services.AddSupportPocApplicationInsights(builder.Configuration);
var entraEnabled = builder.Configuration.IsEntraEnabled();
if (entraEnabled)
{
    builder.Services.AddSupportPocEntraAuth(builder.Configuration);
    builder.Services.AddSupportPocClientCredentials(builder.Configuration);
}
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IFunctionInvocationFilter, McpRoleInvocationFilter>();
builder.Services.AddSupportPocMessagingOptions(builder.Configuration);
builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<ServiceEndpointsOptions>(builder.Configuration.GetSection(ServiceEndpointsOptions.SectionName));
builder.Services.Configure<AutoSuggestionOptions>(builder.Configuration.GetSection(AutoSuggestionOptions.SectionName));
var orchestratorConnectionString = builder.Configuration.GetConnectionString("Orchestrator")
    ?? DatabaseProvider.DefaultOrchestratorConnection;
builder.Services.AddDbContext<OrchestratorDbContext>(options =>
    DatabaseProvider.ConfigureDbContext(options, orchestratorConnectionString));
var openAi = builder.Configuration.GetSection(AzureOpenAIOptions.SectionName).Get<AzureOpenAIOptions>() ?? new AzureOpenAIOptions();
if (openAi.Enabled)
{
    builder.Services.AddKernel()
        .AddAzureOpenAIChatCompletion(openAi.ChatDeployment, openAi.ChatEndpointResolved, openAi.ChatApiKeyResolved);
}
else
{
    builder.Services.AddKernel();
}
var mcpClientRegistration = builder.Services.AddHttpClient(McpToolGateway.HttpClientName)
    .AddSupportPocEntraBearerWhenEnabled(entraEnabled);
var knowledgeClientRegistration = builder.Services.AddHttpClient(KnowledgeSearchClient.HttpClientName)
    .AddSupportPocEntraBearerWhenEnabled(entraEnabled);
builder.Services.AddHttpClient(HttpTicketSnapshotClient.HttpClientName)
    .ConfigureHttpClient((sp, client) =>
    {
        var reconcileOpts = sp.GetRequiredService<IOptions<AutoSuggestionOptions>>().Value;
        client.Timeout = TimeSpan.FromSeconds(Math.Max(15, reconcileOpts.ReconcileHttpTimeoutSeconds + 5));
    })
    .AddSupportPocEntraBearerWhenEnabled(entraEnabled);
builder.Services.AddSingleton<McpToolGateway>();
builder.Services.AddSingleton<McpDynamicPluginLoader>();
builder.Services.AddSingleton<McpToolAccessService>();
builder.Services.AddSingleton<KnowledgeSearchClient>();
builder.Services.AddSingleton<IKnowledgeSearchClient>(sp => sp.GetRequiredService<KnowledgeSearchClient>());
builder.Services.AddScoped<AiPipelineService>();
builder.Services.AddScoped<IAiPipelineService>(sp => sp.GetRequiredService<AiPipelineService>());
builder.Services.AddScoped<IChatCompletionServiceAccessor>();
builder.Services.AddScoped<TicketSuggestionService>();
builder.Services.AddScoped<ITicketSnapshotClient, HttpTicketSnapshotClient>();
builder.Services.AddScoped<ITicketSuggestionReconcileClient, HttpTicketSuggestionReconcileClient>();
builder.Services.AddScoped<IAiGenerationAttemptReader, AiGenerationAttemptReader>();
builder.Services.AddScoped<IAiGenerationAttemptLifecycle, AiGenerationAttemptLifecycle>();
builder.Services.AddScoped<PrepareGenerationRetryActivity>();
builder.Services.AddScoped<ReconcileTicketSuggestionActivity>();
builder.Services.AddScoped<ReconcileUnknownRedriveActivity>();
builder.Services.AddScoped<ISagaReconcileFailureStore, SagaReconcileFailureStore>();
builder.Services.AddScoped<ISagaReconciliationQueue, SagaReconciliationQueue>();
builder.Services.AddScoped<AiGenerationFinalizer>();
builder.Services.AddHostedService<StuckSagaSweeperService>();
builder.Services.AddHostedService<AiGenerationWorkerService>();
var serviceBus = builder.Configuration.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>() ?? new ServiceBusOptions();
var duplicateDetectionWindow = TimeSpan.FromHours(1);
builder.Services.AddMassTransit(mt =>
{
    mt.AddDelayedMessageScheduler();
    mt.AddConsumer<GenerateSuggestionRequestedConsumer, GenerateSuggestionRequestedConsumerDefinition>();
    mt.AddSagaStateMachine<TicketSuggestionStateMachine, TicketSuggestionSaga, TicketSuggestionStateMachineDefinition>()
        .EntityFrameworkRepository(r =>
        {
            r.ConcurrencyMode = ConcurrencyMode.Optimistic;
            r.AddDbContext<DbContext, OrchestratorDbContext>((provider, cfg) =>
            {
                var conn = provider.GetRequiredService<IConfiguration>().GetConnectionString("Orchestrator")
                    ?? DatabaseProvider.DefaultOrchestratorConnection;
                DatabaseProvider.ConfigureDbContext(cfg, conn);
            });
        });
    // Bus outbox + consumer outbox/InboxState (MessageId dedup). AiGenerationAttempts = idempotency theo AttemptId.
    mt.AddEntityFrameworkOutbox<OrchestratorDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
        o.DuplicateDetectionWindow = duplicateDetectionWindow;
    });
    mt.AddEntityFrameworkConsumerOutbox<OrchestratorDbContext>();
    if (serviceBus.Enabled)
    {
        mt.AddSupportPocAzureServiceBusHost(serviceBus, (ctx, cfg) =>
        {
            cfg.UseServiceBusMessageScheduler();
            EndpointConvention.Map<IProposeTicketSuggestion>(new Uri("queue:propose-ticket-suggestion"));
            EndpointConvention.Map<IGenerateSuggestionRequested>(new Uri("queue:generate-suggestion-requested"));
            cfg.ConfigureEndpoints(ctx);
        });
    }
    else
    {
        mt.UsingInMemory((ctx, cfg) =>
        {
            cfg.UseDelayedMessageScheduler();
            EndpointConvention.Map<IProposeTicketSuggestion>(new Uri("queue:propose-ticket-suggestion"));
            EndpointConvention.Map<IGenerateSuggestionRequested>(new Uri("queue:generate-suggestion-requested"));
            cfg.ConfigureEndpoints(ctx);
        });
    }
});
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.Headers.AccessControlAllowOrigin = context.Request.Headers.Origin.ToString();
        context.Response.Headers.AccessControlAllowHeaders = "authorization,content-type";
        context.Response.Headers.AccessControlAllowMethods = "GET,POST,OPTIONS";
        context.Response.Headers.AccessControlAllowCredentials = "true";
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return;
    }

    await next();
});
app.UseCors();
if (entraEnabled)
    app.UseSupportPocEntraAuth();
if (entraEnabled)
{
    var azureAd = app.Configuration.GetSection(AzureAdOptions.SectionName).Get<AzureAdOptions>() ?? new AzureAdOptions();
    var outboundConfig = EntraOutboundReadinessPolicy.EvaluateConfig(azureAd);
    if (!outboundConfig.Ready)
    {
        app.Logger.LogWarning(
            "Entra outbound chua san sang — HTTP outbound (MCP/Knowledge/debug) co the bi 401: {Detail}",
            outboundConfig.Detail);
    }
}
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    await DatabaseProvider.EnsureDatabaseReadyAsync(db);
    await OrchestratorSchemaPatcher.ApplyAsync(db);
}
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ai-orchestrator" }))
    .AllowAnonymous();
app.MapGet("/ready", async (
    HttpContext httpContext,
    IOptions<ServiceBusOptions> sbOpts,
    IOptions<AzureAdOptions> azureAdOpts,
    CancellationToken cancellationToken) =>
{
    var pipeline = await MessagingReadinessPolicy.EvaluatePipelineAsync(sbOpts.Value, cancellationToken);
    if (!pipeline.Ready)
        return Results.Json(
            new { ready = false, transport = pipeline.Transport, detail = pipeline.Detail },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var azureAd = azureAdOpts.Value;
    var entraOutbound = await EntraOutboundReadinessPolicy.EvaluateTokenAsync(
        httpContext.RequestServices,
        azureAd,
        cancellationToken);
    if (azureAd.Enabled && !entraOutbound.Ready)
        return Results.Json(
            new
            {
                ready = false,
                transport = pipeline.Transport,
                detail = pipeline.Detail,
                entraOutbound = new { ready = false, entraOutbound.Detail },
                note = "Entra outbound can Bearer token cho HTTP client noi bo."
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    return Results.Ok(new
    {
        ready = true,
        transport = pipeline.Transport,
        detail = pipeline.Detail,
        entraOutbound = new
        {
            ready = entraOutbound.Ready,
            entraOutbound.Detail,
            ticketSnapshotClient = "EntraBearerTokenHandler"
        },
        messaging = new
        {
            busOutbox = true,
            consumerOutbox = true,
            duplicateDetectionWindow = duplicateDetectionWindow.ToString(),
            note = MessagingOutboxDiagnostics.ConsumerOutboxNote,
            businessIdempotency = "AiGenerationAttempts (AttemptId)"
        },
        note = "Kiem tra transport/DNS va Entra outbound (neu bat). Khong chung minh consumer delivery end-to-end."
    });
}).AllowAnonymous();

app.MapGet("/debug/chat-ready", (
    IOptions<AzureOpenAIOptions> openAiOpts,
    IOptions<ServiceEndpointsOptions> serviceEndpoints,
    IOptions<AzureAdOptions> azureAdOpts) =>
{
    var openAi = openAiOpts.Value;
    string? ResolveHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : value;
    }

    var azureAd = azureAdOpts.Value;
    var mcpDisabled = serviceEndpoints.Value.IsMcpToolServerDisabled;
    return Results.Ok(new
    {
        ready = openAi.Enabled,
        openAi = new
        {
            enabled = openAi.Enabled,
            configured = openAi.ChatConfigured,
            chatEnabled = openAi.ChatEnabledResolved,
            chatDeployment = openAi.ChatDeployment,
            endpointConfigured = !string.IsNullOrWhiteSpace(openAi.ChatEndpointResolved),
            apiKeyConfigured = !string.IsNullOrWhiteSpace(openAi.ChatApiKeyResolved),
            endpointHost = ResolveHost(openAi.ChatEndpointResolved)
        },
        services = new
        {
            knowledgeServiceHost = ResolveHost(serviceEndpoints.Value.KnowledgeService),
            mcp = new
            {
                enabled = !mcpDisabled,
                host = mcpDisabled ? null : ResolveHost(serviceEndpoints.Value.McpToolServer),
                note = mcpDisabled
                    ? "MCP tool server disabled by configuration."
                    : "MCP tool server enabled."
            }
        },
        entra = new
        {
            enabled = azureAd.Enabled,
            tenantId = azureAd.TenantId,
            clientId = azureAd.ClientId,
            audience = azureAd.Audience,
            scope = azureAd.Scope
        },
        note = "Chi bao config readiness; khong chung minh OpenAI hop le cho prompt cu the."
    });
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);
static string MapSagaStatusForUi(string? currentState) => currentState switch
{
    SagaProcessState.GeneratingSuggestion => AutoSuggestionJobStatus.Running,
    SagaProcessState.ApplyingSuggestion => AutoSuggestionJobStatus.Produced,
    SagaProcessState.Reconciling => AutoSuggestionJobStatus.Running,
    SagaProcessState.ReconcileUnknown => AutoSuggestionJobStatus.Unknown,
    SagaProcessState.Completed => AutoSuggestionJobStatus.Completed,
    SagaProcessState.Discarded => AutoSuggestionJobStatus.Discarded,
    SagaProcessState.Failed => AutoSuggestionJobStatus.Failed,
    _ => AutoSuggestionJobStatus.Running
};
app.MapGet("/ops/saga-health", async (OrchestratorDbContext db, IOptions<AutoSuggestionOptions> autoOpts) =>
{
    var now = DateTimeOffset.UtcNow;
    var monitored = await db.TicketSuggestionSagas
        .Where(s =>
            s.CurrentState == SagaProcessState.Reconciling
            || s.CurrentState == SagaProcessState.GeneratingSuggestion
            || s.CurrentState == SagaProcessState.ApplyingSuggestion)
        .ToListAsync();
    var reconciling = monitored.Where(s => s.CurrentState == SagaProcessState.Reconciling).ToList();
    var reconcileUnknown = await db.TicketSuggestionSagas
        .Where(s => s.CurrentState == SagaProcessState.ReconcileUnknown)
        .ToListAsync();
    var unknownItems = reconcileUnknown.Count == 0
        ? []
        : await db.SagaReconciliationItems
            .Where(x => reconcileUnknown.Select(s => s.CorrelationId).Contains(x.SagaId))
            .ToListAsync();
    var generating = monitored.Where(s => s.CurrentState == SagaProcessState.GeneratingSuggestion).ToList();
    var applying = monitored.Where(s => s.CurrentState == SagaProcessState.ApplyingSuggestion).ToList();
    var retryAfter = TimeSpan.FromMinutes(Math.Max(1, autoOpts.Value.StuckReconcilingRetryAfterMinutes));
    var failAfter = TimeSpan.FromMinutes(Math.Max(autoOpts.Value.StuckReconcilingRetryAfterMinutes + 1, autoOpts.Value.StuckReconcilingFailAfterMinutes));
    var stuckStepAfter = TimeSpan.FromMinutes(Math.Max(1, autoOpts.Value.StuckStepSweepAfterMinutes));
    static double GetEffectiveAgeMinutes(TicketSuggestionSaga s, DateTimeOffset n) =>
        s.CurrentState == SagaProcessState.Reconciling
            ? SupportPoc.AiOrchestrator.Services.ReconcileTransientTracker.GetReconcilingAge(s, n).TotalMinutes
            : (n - s.UpdatedAt).TotalMinutes;

    var stale = reconciling.Where(s => ReconcileTransientTracker.GetReconcilingAge(s, now) >= retryAfter).ToList();
    var critical = reconciling.Where(s => ReconcileTransientTracker.GetReconcilingAge(s, now) >= failAfter).ToList();
    var stuckSteps = monitored
        .Where(s => s.CurrentState is SagaProcessState.GeneratingSuggestion or SagaProcessState.ApplyingSuggestion)
        .Where(s => now - s.UpdatedAt >= stuckStepAfter)
        .ToList();
    return Results.Ok(new
    {
        reconcilingCount = reconciling.Count,
        reconcileUnknownCount = reconcileUnknown.Count,
        reconcileUnknownPendingRedrive = unknownItems.Count(x => x.ResolvedAt is null && x.AttemptCount < autoOpts.Value.MaxReconcileUnknownRedriveAttempts),
        reconcileUnknownExhausted = unknownItems.Count(x => x.ResolvedAt is null && x.AttemptCount >= autoOpts.Value.MaxReconcileUnknownRedriveAttempts),
        generatingCount = generating.Count,
        applyingCount = applying.Count,
        stuckStepCount = stuckSteps.Count,
        staleCount = stale.Count,
        criticalCount = critical.Count,
        retryAfterMinutes = retryAfter.TotalMinutes,
        failAfterMinutes = failAfter.TotalMinutes,
        stuckStepAfterMinutes = stuckStepAfter.TotalMinutes,
        samples = stale
            .Concat(stuckSteps)
            .OrderByDescending(s => GetEffectiveAgeMinutes(s, now))
            .Take(20)
            .Select(s => new
            {
                s.CorrelationId,
                s.TicketId,
                s.JobId,
                ageMinutes = GetEffectiveAgeMinutes(s, now),
                s.PendingReconcileAction,
                s.UpdatedAt,
                reconcilingSinceAt = s.ReconcilingSinceAt
            })
    });
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapGet("/ops/reconcile-unknown", async (
    OrchestratorDbContext db,
    IOptions<AutoSuggestionOptions> autoOpts,
    ISagaReconciliationQueue reconciliationQueue,
    int? take) =>
{
    var limit = Math.Clamp(take ?? 50, 1, 200);
    var now = DateTimeOffset.UtcNow;
    var opts = autoOpts.Value;
    var unknownSagas = await db.TicketSuggestionSagas
        .Where(s => s.CurrentState == SagaProcessState.ReconcileUnknown)
        .ToListAsync();

    if (unknownSagas.Count > 0)
        await reconciliationQueue.BackfillMissingItemsAsync(unknownSagas);

    var items = unknownSagas.Count == 0
        ? new Dictionary<Guid, SagaReconciliationItem>()
        : await db.SagaReconciliationItems
            .Where(x => unknownSagas.Select(s => s.CorrelationId).Contains(x.SagaId))
            .ToDictionaryAsync(x => x.SagaId);

    var views = ReconcileUnknownProjection
        .OrderForOps(unknownSagas.Select(s => ReconcileUnknownProjection.Project(
            s,
            items.GetValueOrDefault(s.CorrelationId),
            opts,
            now)))
        .Take(limit)
        .Select(v => new
        {
            sagaId = v.SagaId,
            correlationId = v.SagaId,
            v.TicketId,
            jobId = v.JobId,
            failureReason = v.FailureReason,
            createdAt = v.CreatedAt,
            updatedAt = v.UpdatedAt,
            reconciliationCreatedAt = v.ReconciliationCreatedAt,
            lastAttemptAt = v.LastAttemptAt,
            attemptCount = v.AttemptCount,
            maxAutoRedriveAttempts = v.MaxAutoRedriveAttempts,
            status = v.Status,
            nextAutoRedriveEligibleAt = v.NextAutoRedriveEligibleAt
        })
        .ToList();

    return Results.Ok(new
    {
        count = views.Count,
        totalUnknown = unknownSagas.Count,
        maxAutoRedriveAttempts = Math.Max(1, opts.MaxReconcileUnknownRedriveAttempts),
        items = views
    });
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapPost("/ops/sagas/{sagaId:guid}/redrive-reconcile", async (
    Guid sagaId,
    HttpContext httpContext,
    OrchestratorDbContext db,
    IPublishEndpoint publish,
    ILoggerFactory loggerFactory) =>
{
    var saga = await db.TicketSuggestionSagas
        .FirstOrDefaultAsync(s => s.CorrelationId == sagaId);
    if (saga is null)
        return Results.NotFound();
    if (!ReconcileUnknownRedrivePolicy.IsEligible(saga))
        return Results.BadRequest(new
        {
            error = "Redrive is only allowed for sagas in ReconcileUnknown state.",
            sagaState = saga.CurrentState
        });

    var callerIdentity = OpsCallerIdentity.Resolve(httpContext.User, app.Environment);
    // Manual redrive intentionally does not increment auto-redrive attempt count so ops can recover
    // exhausted sagas without being blocked by MaxReconcileUnknownRedriveAttempts.
    var logger = loggerFactory.CreateLogger("OpsManualRedrive");
    logger.LogWarning(
        "Manual ReconcileUnknown redrive requested SagaId={SagaId} TicketId={TicketId} JobId={JobId} SagaState={SagaState} Caller={Caller}",
        saga.CorrelationId,
        saga.TicketId,
        saga.JobId,
        saga.CurrentState,
        callerIdentity);

    var telemetry = httpContext.RequestServices.TryGetTelemetryClient();
    SagaReconcileTelemetry.TrackUnknownManualRedrive(telemetry, saga.CorrelationId, saga.TicketId, callerIdentity);

    await publish.Publish<IReconcileRedrive>(new ReconcileRedrive(sagaId));
    return Results.Ok(new
    {
        sagaId,
        saga.TicketId,
        jobId = saga.JobId,
        status = MapSagaStatusForUi(saga.CurrentState),
        sagaState = saga.CurrentState,
        failureReason = saga.FailureReason,
        redriveRequestedBy = callerIdentity
    });
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapGet("/tickets/{ticketId}/auto-suggestion", async (string ticketId, OrchestratorDbContext db) =>
{
    var sagas = await db.TicketSuggestionSagas
        .Where(s => s.TicketId == ticketId)
        .ToListAsync();
    var saga = sagas.OrderByDescending(s => s.CreatedAt).FirstOrDefault();
    if (saga is null)
        return Results.NotFound();
    return Results.Ok(new
    {
        jobId = saga.JobId,
        saga.TicketId,
        status = MapSagaStatusForUi(saga.CurrentState),
        sagaState = saga.CurrentState,
        saga.CorrelationId,
        saga.CurrentAttemptId,
        failureReason = saga.FailureReason,
        discardReason = saga.DiscardReason,
        lateMessageAudit = saga.LateMessageAudit,
        saga.CreatedAt,
        saga.UpdatedAt
    });
}).WithUserFacingPolicy(entraEnabled, app.Environment, PolicyNames.AgentOrService);
app.MapGet("/mcp/tools", async (McpDynamicPluginLoader loader, CancellationToken ct) =>
{
    var catalog = await loader.LoadCatalogAsync(ct);
    return Results.Ok(new
    {
        count = catalog.Tools.Count,
        tools = catalog.Tools.Select(t => new { name = t.Name, description = t.Description })
    });
}).WithEntraPolicy(entraEnabled, PolicyNames.AgentOrService);
app.MapGet("/mcp/allowed-tools", async (McpToolAccessService access, HttpContext httpContext, CancellationToken ct) =>
{
    var roles = httpContext.User.FindAll(ClaimTypes.Role).Select(c => c.Value);
    var allowed = await access.GetAllowedToolNamesAsync(roles, ct);
    return Results.Ok(new { count = allowed.Count, tools = allowed.OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase) });
}).WithEntraPolicy(entraEnabled, PolicyNames.EmployeeOrAbove);
app.MapGet("/debug/dlq", async (string? queue, IOptions<ServiceBusOptions> sbOpts) =>
{
    var queueName = string.IsNullOrWhiteSpace(queue) ? "generate-suggestion-requested" : queue.Trim();
    var conn = sbOpts.Value.GetAdministrationConnectionString();
    if (string.IsNullOrWhiteSpace(conn))
        return Results.Ok(new { error = "ServiceBus.ConnectionString chua duoc cau hinh.", queue = queueName });
    try
    {
        var admin = new ServiceBusAdministrationClient(conn);
        async Task<QueueStats> ReadAsync(string name)
        {
            try
            {
                var props = await admin.GetQueueRuntimePropertiesAsync(name);
                return new QueueStats(
                    name,
                    true,
                    props.Value.ActiveMessageCount,
                    props.Value.DeadLetterMessageCount,
                    props.Value.ScheduledMessageCount,
                    props.Value.TransferDeadLetterMessageCount,
                    props.Value.TotalMessageCount);
            }
            catch (Azure.Messaging.ServiceBus.ServiceBusException)
            {
                return new QueueStats(name, false, 0, 0, 0, 0, 0);
            }
        }
        var main = await ReadAsync(queueName);
        var error = await ReadAsync(queueName + "_error");
        return Results.Ok(new
        {
            queue = queueName,
            mainQueue = main,
            errorQueue = error,
            totalFailedMessages = main.DeadLetterMessageCount + error.ActiveMessageCount
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message, queue = queueName });
    }
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);
app.MapGet("/debug/messaging", async (OrchestratorDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await MessagingOutboxDiagnostics.BuildSnapshotAsync(
        db, duplicateDetectionWindow, cancellationToken: cancellationToken)))
    .WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapGet("/debug/inbox", async (OrchestratorDbContext db) =>
{
    var items = await db.Set<InboxState>()
        .OrderByDescending(x => x.Received)
        .Take(50)
        .Select(x => new { x.MessageId, x.ConsumerId, x.Received, x.Consumed, x.ReceiveCount })
        .ToListAsync();
    return Results.Ok(items);
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapGet("/debug/ai-generation-attempts", async (string? ticketId, Guid? attemptId, OrchestratorDbContext db) =>
{
    var query = db.AiGenerationAttempts.AsQueryable();
    if (!string.IsNullOrWhiteSpace(ticketId))
        query = query.Where(x => x.TicketId == ticketId);
    if (attemptId is not null)
        query = query.Where(x => x.AttemptId == attemptId);
    var items = (await query.ToListAsync())
        .OrderByDescending(x => x.UpdatedAt)
        .Take(50)
        .Select(x => new
        {
            x.AttemptId,
            x.SagaId,
            x.JobId,
            x.TicketId,
            x.Status,
            x.Category,
            hasSuggestion = !string.IsNullOrWhiteSpace(x.Suggestion),
            x.Error,
            x.RetryCount,
            x.NextRunAt,
            x.LeaseOwner,
            x.LeaseUntil,
            x.StartedAt,
            x.CompletedAt,
            x.UpdatedAt
        })
        .ToList();
    return Results.Ok(items);
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapGet("/debug/saga-instances", async (string? ticketId, OrchestratorDbContext db) =>
{
    var query = db.TicketSuggestionSagas.AsQueryable();
    if (!string.IsNullOrWhiteSpace(ticketId))
        query = query.Where(x => x.TicketId == ticketId);
    var items = (await query.ToListAsync())
        .OrderByDescending(x => x.UpdatedAt)
        .Take(100)
        .Select(x => new
        {
            x.CorrelationId,
            x.JobId,
            x.TicketId,
            x.CurrentState,
            x.CurrentAttemptId,
            x.RetryCount,
            x.FailureReason,
            x.DiscardReason,
            x.LateMessageAudit,
            x.CreatedAt,
            x.UpdatedAt
        })
        .ToList();
    return Results.Ok(items);
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapGet("/debug/ticket-snapshot/{ticketId}", async (
    string ticketId,
    ITicketSnapshotClient snapshotClient,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var snapshot = await snapshotClient.GetTicketAsync(ticketId, cancellationToken);
        return Results.Ok(new
        {
            ok = true,
            entraEnabled = configuration.IsEntraEnabled(),
            ticketId,
            snapshotFound = snapshot is not null,
            snapshot = snapshot is null ? null : new
            {
                snapshot.TicketId,
                snapshot.Status,
                snapshot.HasAiSuggestion,
                snapshot.Version
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                ok = false,
                entraEnabled = configuration.IsEntraEnabled(),
                ticketId,
                error = ex.Message
            },
            statusCode: StatusCodes.Status502BadGateway);
    }
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapPost("/ai/suggest-answer", async (SuggestAnswerRequest request, TicketSuggestionService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "question la bat buoc." });
    var answer = await service.SuggestAnswerAsync(request.Question, request.Category, ct);
    return Results.Ok(new { suggestedAnswer = answer });
}).WithEntraPolicy(entraEnabled, PolicyNames.AgentOrService);
app.MapPost("/ai/chat", async (ChatRequest request, TicketSuggestionService service, HttpContext httpContext, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { error = "message la bat buoc." });
    IEnumerable<string>? roles = entraEnabled
        ? httpContext.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray()
        : null;
    var reply = await service.ChatAsync(request.Message, roles, ct);
    return Results.Ok(new { reply });
}).WithEntraPolicy(entraEnabled, PolicyNames.EmployeeOrAbove);
app.MapPost("/ai/classify-ticket", async (ClassifyRequest request, TicketSuggestionService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "question la bat buoc." });
    var result = await service.ClassifyTicketAsync(request.Question, ct);
    return result is null ? Results.Problem("Khong phan loai duoc.") : Results.Ok(result);
}).WithEntraPolicy(entraEnabled, PolicyNames.Service);
app.Run();
public sealed record SuggestAnswerRequest(string Question, string? Category);
public sealed record ChatRequest(string Message);
public sealed record ClassifyRequest(string Question);
public sealed record QueueStats(
    string Name,
    bool Exists,
    long ActiveMessageCount,
    long DeadLetterMessageCount,
    long ScheduledMessageCount,
    long TransferDeadLetterMessageCount,
    long TotalMessageCount);
