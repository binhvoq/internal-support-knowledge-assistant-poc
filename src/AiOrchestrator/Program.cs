using Microsoft.SemanticKernel;
using SupportPoc.AiOrchestrator.Clients;
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
builder.Services.AddSingleton<TicketSuggestionService>();
builder.Services.AddHttpClient<TicketApiClient>((sp, client) =>
{
    var endpoints = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceEndpointsOptions>>().Value;
    client.BaseAddress = new Uri(endpoints.TicketService.TrimEnd('/') + "/");
});
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ai-orchestrator" }));

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
    await service.ProcessTicketCreatedAsync(request.TicketId, ct);
    return Results.Ok(new { status = "processed", ticketId = request.TicketId });
});

app.Run();

public sealed record SuggestAnswerRequest(string Question, string? Category);
public sealed record ChatRequest(string Message);
public sealed record ClassifyRequest(string Question);
public sealed record TicketCreatedInternalRequest(string TicketId);
