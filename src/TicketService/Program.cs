using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.Shared.Events;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.AddSingleton<ISupportEventPublisher, ServiceBusEventPublisher>();
builder.Services.AddHttpClient<AiOrchestratorNotifier>();
builder.Services.AddDbContext<TicketDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Tickets") ?? "Data Source=tickets.db"));
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
    await db.Database.EnsureCreatedAsync();
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ticket-service" }));

app.MapPost("/tickets", async (CreateTicketRequest request, TicketDbContext db, ISupportEventPublisher publisher, AiOrchestratorNotifier aiNotifier, IOptions<ServiceBusOptions> busOptions) =>
{
    if (string.IsNullOrWhiteSpace(request.EmployeeId) || string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "employeeId va question la bat buoc." });

    var category = string.IsNullOrWhiteSpace(request.Category) ? SupportCategory.Other : request.Category;
    var now = DateTimeOffset.UtcNow;
    var ids = await db.Tickets.Select(t => t.Id).ToListAsync();
    var entity = new TicketEntity
    {
        Id = TicketIdGenerator.Next(ids),
        EmployeeId = request.EmployeeId.Trim(),
        Category = category,
        Question = request.Question.Trim(),
        Status = TicketStatus.New,
        CreatedAt = now,
        UpdatedAt = now
    };

    db.Tickets.Add(entity);
    await db.SaveChangesAsync();
    var published = await TryPublishAsync(app.Logger, publisher, SupportEventTypes.TicketCreated, new TicketCreatedPayload { TicketId = entity.Id });
    if (!busOptions.Value.Enabled || !published)
        _ = aiNotifier.NotifyTicketCreatedAsync(entity.Id);

    return Results.Created($"/tickets/{entity.Id}", TicketMapper.ToDto(entity));
});

app.MapGet("/tickets", async (string? status, string? category, TicketDbContext db) =>
{
    var query = db.Tickets.AsQueryable();
    if (!string.IsNullOrWhiteSpace(status))
        query = query.Where(t => t.Status == status);
    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(t => t.Category == category);

    var items = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
    return Results.Ok(items.Select(TicketMapper.ToDto));
});

app.MapGet("/tickets/{id}", async (string id, TicketDbContext db) =>
{
    var entity = await db.Tickets.FindAsync(id);
    return entity is null ? Results.NotFound() : Results.Ok(TicketMapper.ToDto(entity));
});

app.MapPatch("/tickets/{id}", async (string id, UpdateTicketRequest request, TicketDbContext db, ISupportEventPublisher publisher) =>
{
    var entity = await db.Tickets.FindAsync(id);
    if (entity is null) return Results.NotFound();

    if (!string.IsNullOrWhiteSpace(request.Status))
        entity.Status = request.Status;
    if (request.AiSuggestedAnswer is not null)
        entity.AiSuggestedAnswer = request.AiSuggestedAnswer;
    if (request.FinalAnswer is not null)
        entity.FinalAnswer = request.FinalAnswer;
    if (request.Category is not null)
        entity.Category = request.Category;
    if (request.RelatedDocuments is not null)
        entity.RelatedDocumentsJson = JsonSerializer.Serialize(request.RelatedDocuments, jsonOptions);

    entity.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    await TryPublishAsync(app.Logger, publisher, SupportEventTypes.TicketUpdated, new { ticketId = id });

    return Results.Ok(TicketMapper.ToDto(entity));
});

app.MapPost("/tickets/{id}/resolve", async (string id, ResolveTicketRequest request, TicketDbContext db, ISupportEventPublisher publisher) =>
{
    var entity = await db.Tickets.FindAsync(id);
    if (entity is null) return Results.NotFound();

    entity.Status = TicketStatus.Resolved;
    entity.FinalAnswer = request.FinalAnswer?.Trim() ?? entity.AiSuggestedAnswer;
    entity.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    await TryPublishAsync(app.Logger, publisher, SupportEventTypes.TicketResolved, new TicketResolvedPayload { TicketId = id });

    return Results.Ok(TicketMapper.ToDto(entity));
});

app.MapPost("/tickets/{id}/reopen", async (string id, TicketDbContext db, ISupportEventPublisher publisher) =>
{
    var entity = await db.Tickets.FindAsync(id);
    if (entity is null) return Results.NotFound();

    entity.Status = TicketStatus.Reopened;
    entity.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync();
    await TryPublishAsync(app.Logger, publisher, SupportEventTypes.TicketUpdated, new { ticketId = id, status = TicketStatus.Reopened });
    return Results.Ok(TicketMapper.ToDto(entity));
});

app.Run();

static async Task<bool> TryPublishAsync<TPayload>(
    ILogger logger,
    ISupportEventPublisher publisher,
    string eventType,
    TPayload payload,
    CancellationToken cancellationToken = default)
{
    try
    {
        await publisher.PublishAsync(eventType, payload, cancellationToken);
        return true;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Publish event {EventType} that bai; tiep tuc local flow.", eventType);
        return false;
    }
}

public sealed record CreateTicketRequest(string EmployeeId, string Question, string? Category);
public sealed record UpdateTicketRequest(
    string? Status,
    string? AiSuggestedAnswer,
    string? FinalAnswer,
    string? Category,
    IReadOnlyList<RelatedDocument>? RelatedDocuments);
public sealed record ResolveTicketRequest(string? FinalAnswer);
