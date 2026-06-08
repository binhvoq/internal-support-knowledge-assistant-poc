using System.Security.Claims;
using Azure.Messaging.ServiceBus.Administration;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using SupportPoc.AiOrchestrator.Consumers;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Mcp;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Auth;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Messaging;
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
builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<ServiceEndpointsOptions>(builder.Configuration.GetSection(ServiceEndpointsOptions.SectionName));
builder.Services.Configure<AutoSuggestionOptions>(builder.Configuration.GetSection(AutoSuggestionOptions.SectionName));
builder.Services.AddDbContext<OrchestratorDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Orchestrator") ?? "Data Source=orchestrator.db"));
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
var mcpClientRegistration = builder.Services.AddHttpClient(McpToolGateway.HttpClientName);
if (entraEnabled)
    mcpClientRegistration.AddHttpMessageHandler<EntraBearerTokenHandler>();
var knowledgeClientRegistration = builder.Services.AddHttpClient(KnowledgeSearchClient.HttpClientName);
if (entraEnabled)
    knowledgeClientRegistration.AddHttpMessageHandler<EntraBearerTokenHandler>();
builder.Services.AddSingleton<McpToolGateway>();
builder.Services.AddSingleton<McpDynamicPluginLoader>();
builder.Services.AddSingleton<McpToolAccessService>();
builder.Services.AddSingleton<KnowledgeSearchClient>();
builder.Services.AddSingleton<IKnowledgeSearchClient>(sp => sp.GetRequiredService<KnowledgeSearchClient>());
builder.Services.AddScoped<AiPipelineService>();
builder.Services.AddScoped<IChatCompletionServiceAccessor>();
builder.Services.AddScoped<TicketSuggestionService>();
var serviceBus = builder.Configuration.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>() ?? new ServiceBusOptions();
builder.Services.Configure<LocalMessagingOptions>(builder.Configuration.GetSection(LocalMessagingOptions.SectionName));
var localMessaging = builder.Configuration.GetSection(LocalMessagingOptions.SectionName).Get<LocalMessagingOptions>() ?? new LocalMessagingOptions();
builder.Services.AddHttpClient(HttpConsiderAutoSuggestionGateway.HttpClientName);
if (localMessaging.HttpBridgeEnabled && !serviceBus.Enabled)
    builder.Services.AddScoped<IConsiderAutoSuggestionGateway, HttpConsiderAutoSuggestionGateway>();
else
    builder.Services.AddScoped<IConsiderAutoSuggestionGateway, MassTransitConsiderAutoSuggestionGateway>();
builder.Services.AddMassTransit(mt =>
{
    mt.AddConsumer<TicketCreatedConsumer, TicketCreatedConsumerDefinition>();
    mt.AddEntityFrameworkOutbox<OrchestratorDbContext>(o =>
    {
        o.UseSqlite();
        o.UseBusOutbox();
        o.DuplicateDetectionWindow = TimeSpan.FromHours(1);
    });
    if (serviceBus.Enabled)
    {
        mt.UsingAzureServiceBus((ctx, cfg) =>
        {
            cfg.Host(serviceBus.ConnectionString);
            EndpointConvention.Map<IConsiderAutoSuggestion>(new Uri("queue:consider-auto-suggestion"));
            cfg.ConfigureEndpoints(ctx);
        });
    }
    else
    {
        mt.UsingInMemory((ctx, cfg) =>
        {
            EndpointConvention.Map<IConsiderAutoSuggestion>(new Uri("queue:consider-auto-suggestion"));
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
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    await db.Database.EnsureCreatedAsync();
    await OrchestratorDbSchema.EnsureSchemaAsync(db);
}
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ai-orchestrator" }))
    .AllowAnonymous();
app.MapGet("/ready", async (
    IHostEnvironment env,
    IOptions<ServiceBusOptions> sbOpts,
    IOptions<LocalMessagingOptions> localOpts,
    CancellationToken cancellationToken) =>
{
    var pipeline = await DevBridgeEndpointPolicy.EvaluatePipelineAsync(
        env, sbOpts.Value, localOpts.Value, cancellationToken);
    if (!pipeline.Ready)
        return Results.Json(
            new { ready = false, transport = pipeline.Transport, detail = pipeline.Detail, httpBridge = pipeline.HttpBridge },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    return Results.Ok(new
    {
        ready = true,
        transport = pipeline.Transport,
        detail = pipeline.Detail,
        httpBridge = pipeline.HttpBridge,
        note = "Chi kiem tra transport/DNS hoac cau hinh bridge — khong chung minh consumer dang chay. Dung smoke-test cho end-to-end."
    });
}).AllowAnonymous();
if (DevBridgeEndpointPolicy.IsEnabled(app.Environment, localMessaging, serviceBus))
{
    app.MapPost("/internal/dev/ticket-created", async (HttpContext httpContext, TicketCreated message, IBus bus) =>
    {
        if (!DevBridgeEndpointPolicy.IsLocalCaller(httpContext))
            return DevBridgeEndpointPolicy.RejectDisabled();

        await bus.Publish<ITicketCreated>(message);
        return Results.Accepted();
    }).AllowAnonymous();
}
app.MapGet("/tickets/{ticketId}/auto-suggestion", async (string ticketId, OrchestratorDbContext db) =>
{
    var jobs = await db.AutoSuggestionJobs
        .Where(j => j.TicketId == ticketId)
        .ToListAsync();
    var job = jobs.OrderByDescending(j => j.CreatedAt).FirstOrDefault();
    if (job is null)
        return Results.NotFound();
    return Results.Ok(new
    {
        job.JobId,
        job.TicketId,
        job.Status,
        job.FailureReason,
        job.DiscardReason,
        job.CreatedAt,
        job.UpdatedAt
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
    var queueName = string.IsNullOrWhiteSpace(queue) ? "auto-suggestion-ticket-created" : queue.Trim();
    var conn = sbOpts.Value.ConnectionString;
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
app.MapGet("/debug/auto-suggestion-jobs", async (string? ticketId, OrchestratorDbContext db) =>
{
    var query = db.AutoSuggestionJobs.AsQueryable();
    if (!string.IsNullOrWhiteSpace(ticketId))
        query = query.Where(x => x.TicketId == ticketId);
    var items = (await query.ToListAsync())
        .OrderByDescending(x => x.UpdatedAt)
        .Take(100)
        .Select(x => new
        {
            x.JobId,
            x.TicketId,
            x.Status,
            x.FailureReason,
            x.DiscardReason,
            x.CreatedAt,
            x.UpdatedAt
        })
        .ToList();
    return Results.Ok(items);
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
