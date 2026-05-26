using Azure.Messaging.ServiceBus.Administration;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using SupportPoc.AiOrchestrator.Consumers;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Mcp;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Saga;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<ServiceEndpointsOptions>(builder.Configuration.GetSection(ServiceEndpointsOptions.SectionName));
builder.Services.Configure<SagaTimeoutOptions>(builder.Configuration.GetSection(SagaTimeoutOptions.SectionName));

builder.Services.AddDbContext<OrchestratorDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Orchestrator") ?? "Data Source=orchestrator.db"));

// ---------- Azure OpenAI / Semantic Kernel ----------
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

builder.Services.AddSingleton<McpToolGateway>();
builder.Services.AddSingleton<McpDynamicPluginLoader>();
builder.Services.AddScoped<AiPipelineService>();
builder.Services.AddScoped<IChatCompletionServiceAccessor>();
builder.Services.AddScoped<TicketSuggestionService>();

// ---------- MassTransit ----------
var serviceBus = builder.Configuration.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>() ?? new ServiceBusOptions();

builder.Services.AddMassTransit(mt =>
{
    // Saga state machine + EF repository (saga state luu chung DbContext).
    mt.AddSagaStateMachine<TicketSuggestionStateMachine, TicketSuggestionState, TicketSuggestionStateDefinition>()
        .EntityFrameworkRepository(r =>
        {
            r.ConcurrencyMode = ConcurrencyMode.Optimistic;
            r.ExistingDbContext<OrchestratorDbContext>();
            r.UseSqlite();
        });

    mt.AddConsumer<RunAiPipelineConsumer, RunAiPipelineConsumerDefinition>();

    // Transactional Outbox - bus-level. Moi Publish/Send tu code se di qua outbox cua DbContext.
    mt.AddEntityFrameworkOutbox<OrchestratorDbContext>(o =>
    {
        o.UseSqlite();
        // Hosted service tu doc Pending outbox row -> publish len broker.
        o.UseBusOutbox();
        // Cleanup InboxState row qua han (default 30 ngay).
        o.DuplicateDetectionWindow = TimeSpan.FromHours(1);
    });

    if (serviceBus.Enabled)
    {
        mt.UsingAzureServiceBus((ctx, cfg) =>
        {
            cfg.Host(serviceBus.ConnectionString);

            // Endpoint convention cho phep .Send<T>() khong can chi destination.
            // Cross-service commands den TicketService:
            EndpointConvention.Map<IMarkTicketAnalyzing>(new Uri("queue:mark-ticket-analyzing"));
            EndpointConvention.Map<ISaveTicketSuggestion>(new Uri("queue:save-ticket-suggestion"));
            EndpointConvention.Map<ICompensateMarkAnalyzing>(new Uri("queue:compensate-mark-analyzing"));
            // Internal command:
            EndpointConvention.Map<IRunAiPipeline>(new Uri("queue:run-ai-pipeline"));

            // Tu dong tao endpoint cho saga + consumer + scheduler.
            cfg.UseServiceBusMessageScheduler();
            cfg.ConfigureEndpoints(ctx);
        });
    }
    else
    {
        // Fallback in-memory transport cho dev khong co Service Bus.
        // Luu y: chi work intra-process - TicketService khong nhan duoc command.
        // InMemory transport co built-in delayed message support (cho saga timeout schedule).
        mt.AddDelayedMessageScheduler();
        mt.UsingInMemory((ctx, cfg) =>
        {
            EndpointConvention.Map<IMarkTicketAnalyzing>(new Uri("queue:mark-ticket-analyzing"));
            EndpointConvention.Map<ISaveTicketSuggestion>(new Uri("queue:save-ticket-suggestion"));
            EndpointConvention.Map<ICompensateMarkAnalyzing>(new Uri("queue:compensate-mark-analyzing"));
            EndpointConvention.Map<IRunAiPipeline>(new Uri("queue:run-ai-pipeline"));

            cfg.UseDelayedMessageScheduler();
            cfg.ConfigureEndpoints(ctx);
        });
    }
});

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ai-orchestrator" }));

app.MapGet("/mcp/tools", async (McpDynamicPluginLoader loader, CancellationToken ct) =>
{
    var catalog = await loader.LoadCatalogAsync(ct);
    return Results.Ok(new
    {
        count = catalog.Tools.Count,
        tools = catalog.Tools.Select(t => new { name = t.Name, description = t.Description })
    });
});

// /debug/dlq doc dead-letter queue cua mot endpoint Azure Service Bus.
// Tra ve count cho queue chinh VA queue phu _error (MassTransit default faulted transport).
// Verify ca 2 vi:
//   - Truoc khi ConfigureDeadLetterQueueErrorTransport: message vao queue '_error'
//   - Sau khi cau hinh: message vao ASB native DLQ cua queue chinh
//   - Co the can ca 2 trong giai doan migration / dev voi queue da ton tai.
app.MapGet("/debug/dlq", async (string? queue, IOptions<ServiceBusOptions> sbOpts) =>
{
    var queueName = string.IsNullOrWhiteSpace(queue) ? "run-ai-pipeline" : queue.Trim();
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
            // Field tong hop: bat ki cho nao tang nghia la failure pattern hoat dong.
            totalFailedMessages = main.DeadLetterMessageCount + error.ActiveMessageCount
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message, queue = queueName });
    }
});

// /debug/saga query MassTransit-managed TicketSuggestionStates thay vi SagaLogEntries cu.
app.MapGet("/debug/saga", async (string? ticketId, OrchestratorDbContext db) =>
{
    var query = db.TicketSuggestionStates.AsQueryable();
    if (!string.IsNullOrWhiteSpace(ticketId))
        query = query.Where(x => x.TicketId == ticketId);
    var items = (await query.ToListAsync())
        .OrderByDescending(x => x.UpdatedAt)
        .Take(100)
        .Select(x => new
        {
            x.CorrelationId,
            x.TicketId,
            x.CurrentState,
            x.Category,
            x.OriginalStatus,
            x.FailureReason,
            x.CompensationReason,
            x.CreatedAt,
            x.UpdatedAt
        })
        .ToList();
    return Results.Ok(items);
});

app.MapPost("/ai/suggest-answer", async (SuggestAnswerRequest request, TicketSuggestionService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "question la bat buoc." });
    var answer = await service.SuggestAnswerAsync(request.Question, request.Category, ct);
    return Results.Ok(new { suggestedAnswer = answer });
});

app.MapPost("/ai/chat", async (ChatRequest request, TicketSuggestionService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { error = "message la bat buoc." });
    var reply = await service.ChatAsync(request.Message, ct);
    return Results.Ok(new { reply });
});

app.MapPost("/ai/classify-ticket", async (ClassifyRequest request, TicketSuggestionService service, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "question la bat buoc." });
    var result = await service.ClassifyTicketAsync(request.Question, ct);
    return result is null ? Results.Problem("Khong phan loai duoc.") : Results.Ok(result);
});

app.Run();

public sealed record SuggestAnswerRequest(string Question, string? Category);
public sealed record ChatRequest(string Message);
public sealed record ClassifyRequest(string Question);

// DTO cho /debug/dlq.
public sealed record QueueStats(
    string Name,
    bool Exists,
    long ActiveMessageCount,
    long DeadLetterMessageCount,
    long ScheduledMessageCount,
    long TransferDeadLetterMessageCount,
    long TotalMessageCount);
