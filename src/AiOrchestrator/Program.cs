using Microsoft.SemanticKernel;
using Microsoft.EntityFrameworkCore;
using SupportPoc.AiOrchestrator.Clients;
using SupportPoc.AiOrchestrator.Data;
using SupportPoc.AiOrchestrator.Mcp;
using SupportPoc.AiOrchestrator.Options;
using SupportPoc.AiOrchestrator.Services;
using SupportPoc.AiOrchestrator.Workers;
using SupportPoc.Shared.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<ServiceEndpointsOptions>(builder.Configuration.GetSection(ServiceEndpointsOptions.SectionName));
builder.Services.AddSingleton<ISupportEventPublisher, ServiceBusEventPublisher>();
builder.Services.AddHostedService<TicketCreatedWorker>();
builder.Services.AddDbContext<OrchestratorDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Orchestrator") ?? "Data Source=orchestrator.db"));
builder.Services.AddScoped<InboxService>();
builder.Services.AddScoped<SagaLogService>();

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
builder.Services.AddScoped<TicketSuggestionService>();
builder.Services.AddHttpClient<TicketApiClient>((sp, client) =>
{
    var endpoints = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceEndpointsOptions>>().Value;
    client.BaseAddress = new Uri(endpoints.TicketService.TrimEnd('/') + "/");
});
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS InboxMessages (
            EventId TEXT NOT NULL,
            Consumer TEXT NOT NULL,
            Status TEXT NOT NULL,
            TicketId TEXT NULL,
            Error TEXT NULL,
            ReceivedAt TEXT NOT NULL,
            ProcessedAt TEXT NULL,
            CONSTRAINT PK_InboxMessages PRIMARY KEY (Consumer, EventId)
        );
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS SagaLogEntries (
            Id TEXT NOT NULL CONSTRAINT PK_SagaLogEntries PRIMARY KEY,
            EventId TEXT NOT NULL,
            TicketId TEXT NOT NULL,
            Step TEXT NOT NULL,
            Status TEXT NOT NULL,
            Detail TEXT NULL,
            CreatedAt TEXT NOT NULL
        );
        """);
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

app.MapGet("/debug/inbox", async (OrchestratorDbContext db) =>
{
    var items = (await db.InboxMessages
        .ToListAsync())
        .OrderByDescending(x => x.ReceivedAt)
        .Take(50)
        .Select(x => new { x.Consumer, x.EventId, x.TicketId, x.Status, x.ReceivedAt, x.ProcessedAt, x.Error })
        .ToList();
    return Results.Ok(items);
});

app.MapGet("/debug/saga", async (string? ticketId, OrchestratorDbContext db) =>
{
    var query = db.SagaLogEntries.AsQueryable();
    if (!string.IsNullOrWhiteSpace(ticketId))
        query = query.Where(x => x.TicketId == ticketId);
    var items = (await query
        .ToListAsync())
        .OrderByDescending(x => x.CreatedAt)
        .Take(100)
        .Select(x => new { x.EventId, x.TicketId, x.Step, x.Status, x.Detail, x.CreatedAt })
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

app.MapPost("/internal/ticket-created", async (TicketCreatedInternalRequest request, TicketSuggestionService service, CancellationToken ct) =>
{
    await service.ProcessTicketCreatedAsync(request.TicketId, request.EventId, ct);
    return Results.Ok(new { status = "processed", ticketId = request.TicketId });
});

app.Run();

public sealed record SuggestAnswerRequest(string Question, string? Category);
public sealed record ChatRequest(string Message);
public sealed record ClassifyRequest(string Question);
public sealed record TicketCreatedInternalRequest(string TicketId, string? EventId);
