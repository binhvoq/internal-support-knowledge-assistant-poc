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
var serviceBus = builder.Configuration.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>() ?? new ServiceBusOptions();
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
    mt.AddEntityFrameworkOutbox<OrchestratorDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
        o.DuplicateDetectionWindow = TimeSpan.FromHours(1);
    });
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
            "Entra outbound chua san sang — saga reconcile snapshot co the bi 401: {Detail}",
            outboundConfig.Detail);
    }
}
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    await DatabaseProvider.EnsureDatabaseReadyAsync(db);
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
                note = "Saga reconcile snapshot can Bearer token cho TicketService."
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
            sagaConsumerOutbox = true,
            aiWorkerConsumerOutbox = true,
            note = "Saga MassTransit Inbox enabled (SQL Server)."
        },
        note = "Kiem tra transport/DNS va Entra outbound (neu bat). Dung smoke-test cho end-to-end."
    });
}).AllowAnonymous();
static string MapSagaStatusForUi(string? currentState) => currentState switch
{
    SagaProcessState.GeneratingSuggestion => AutoSuggestionJobStatus.Running,
    SagaProcessState.ApplyingSuggestion => AutoSuggestionJobStatus.Produced,
    SagaProcessState.Reconciling => AutoSuggestionJobStatus.Running,
    SagaProcessState.Completed => AutoSuggestionJobStatus.Completed,
    SagaProcessState.Discarded => AutoSuggestionJobStatus.Discarded,
    SagaProcessState.Failed => AutoSuggestionJobStatus.Failed,
    _ => AutoSuggestionJobStatus.Running
};
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
}).AllowAnonymous();
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
}).AllowAnonymous();
app.MapGet("/debug/inbox", async (OrchestratorDbContext db) =>
{
    var items = await db.Set<InboxState>()
        .OrderByDescending(x => x.Received)
        .Take(50)
        .Select(x => new { x.MessageId, x.ConsumerId, x.Received, x.Consumed, x.ReceiveCount })
        .ToListAsync();
    return Results.Ok(items);
}).AllowAnonymous();

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
            x.StartedAt,
            x.CompletedAt,
            x.UpdatedAt
        })
        .ToList();
    return Results.Ok(items);
}).AllowAnonymous();

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
}).AllowAnonymous();

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
}).AllowAnonymous();

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
