using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.Shared.Events;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;
using SupportPoc.TicketService.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.AddSingleton<ISupportEventPublisher, ServiceBusEventPublisher>();
builder.Services.AddHttpClient<AiOrchestratorNotifier>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
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
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS OutboxMessages (
            Id TEXT NOT NULL CONSTRAINT PK_OutboxMessages PRIMARY KEY,
            EventId TEXT NOT NULL,
            EventType TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            Status TEXT NOT NULL,
            Error TEXT NULL,
            CreatedAt TEXT NOT NULL,
            PublishedAt TEXT NULL
        );
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE UNIQUE INDEX IF NOT EXISTS IX_OutboxMessages_EventId ON OutboxMessages (EventId);
        """);
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS IdempotencyRecords (
            Key TEXT NOT NULL,
            Scope TEXT NOT NULL,
            RequestHash TEXT NOT NULL,
            ResponseJson TEXT NOT NULL,
            StatusCode INTEGER NOT NULL,
            CreatedAt TEXT NOT NULL,
            CONSTRAINT PK_IdempotencyRecords PRIMARY KEY (Scope, Key)
        );
        """);
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ticket-service" }));

app.MapGet("/debug/outbox", async (TicketDbContext db) =>
{
    var items = (await db.OutboxMessages
        .ToListAsync())
        .OrderByDescending(x => x.CreatedAt)
        .Take(50)
        .Select(x => new { x.EventId, x.EventType, x.Status, x.CreatedAt, x.PublishedAt, x.Error })
        .ToList();
    return Results.Ok(items);
});

app.MapGet("/debug/idempotency", async (TicketDbContext db) =>
{
    var items = (await db.IdempotencyRecords
        .ToListAsync())
        .OrderByDescending(x => x.CreatedAt)
        .Take(50)
        .Select(x => new { x.Scope, x.Key, x.RequestHash, x.StatusCode, x.CreatedAt })
        .ToList();
    return Results.Ok(items);
});

app.MapPost("/tickets", async (
    CreateTicketRequest request,
    HttpContext httpContext,
    TicketDbContext db,
    AiOrchestratorNotifier aiNotifier,
    IOptions<ServiceBusOptions> busOptions) =>
{
    if (string.IsNullOrWhiteSpace(request.EmployeeId) || string.IsNullOrWhiteSpace(request.Question))
        return Results.BadRequest(new { error = "employeeId va question la bat buoc." });

    const string scope = "POST /tickets";
    var idempotencyKey = IdempotencyHelper.ReadKey(httpContext);
    var requestHash = IdempotencyHelper.Hash(scope, request);
    if (!string.IsNullOrWhiteSpace(idempotencyKey))
    {
        var existing = await db.IdempotencyRecords.FindAsync([scope, idempotencyKey], httpContext.RequestAborted);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
                return Results.Conflict(new { error = "Idempotency-Key da duoc dung voi payload khac." });
            app.Logger.LogInformation("Idempotency replay {Scope} Key={Key}", scope, idempotencyKey);
            return IdempotencyHelper.Replay(existing);
        }
    }

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
    db.OutboxMessages.Add(OutboxFactory.Create(SupportEventTypes.TicketCreated, new TicketCreatedPayload { TicketId = entity.Id }));
    var dto = TicketMapper.ToDto(entity);
    if (!string.IsNullOrWhiteSpace(idempotencyKey))
    {
        db.IdempotencyRecords.Add(new IdempotencyRecordEntity
        {
            Scope = scope,
            Key = idempotencyKey,
            RequestHash = requestHash,
            ResponseJson = JsonSerializer.Serialize(dto, jsonOptions),
            StatusCode = StatusCodes.Status201Created,
            CreatedAt = now
        });
    }

    await db.SaveChangesAsync();
    if (!busOptions.Value.Enabled)
        _ = aiNotifier.NotifyTicketCreatedAsync(entity.Id);

    return Results.Created($"/tickets/{entity.Id}", dto);
});

app.MapGet("/tickets", async (string? status, string? category, TicketDbContext db) =>
{
    var query = db.Tickets.AsQueryable();
    if (!string.IsNullOrWhiteSpace(status))
        query = query.Where(t => t.Status == status);
    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(t => t.Category == category);

    var items = (await query.ToListAsync()).OrderByDescending(t => t.CreatedAt).ToList();
    return Results.Ok(items.Select(TicketMapper.ToDto));
});

app.MapGet("/tickets/{id}", async (string id, TicketDbContext db) =>
{
    var entity = await db.Tickets.FindAsync(id);
    return entity is null ? Results.NotFound() : Results.Ok(TicketMapper.ToDto(entity));
});

app.MapPatch("/tickets/{id}", async (string id, UpdateTicketRequest request, TicketDbContext db) =>
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
    db.OutboxMessages.Add(OutboxFactory.Create(SupportEventTypes.TicketUpdated, new { ticketId = id }));
    await db.SaveChangesAsync();

    return Results.Ok(TicketMapper.ToDto(entity));
});

app.MapPost("/tickets/{id}/resolve", async (string id, ResolveTicketRequest request, HttpContext httpContext, TicketDbContext db) =>
{
    var entity = await db.Tickets.FindAsync(id);
    if (entity is null) return Results.NotFound();

    if (string.IsNullOrWhiteSpace(request.FinalAnswer))
        return Results.BadRequest(new { error = "finalAnswer la bat buoc khi resolve ticket." });

    var scope = $"POST /tickets/{id}/resolve";
    var idempotencyKey = IdempotencyHelper.ReadKey(httpContext);
    var requestHash = IdempotencyHelper.Hash(scope, request);
    if (!string.IsNullOrWhiteSpace(idempotencyKey))
    {
        var existing = await db.IdempotencyRecords.FindAsync([scope, idempotencyKey], httpContext.RequestAborted);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
                return Results.Conflict(new { error = "Idempotency-Key da duoc dung voi payload khac." });
            app.Logger.LogInformation("Idempotency replay {Scope} Key={Key}", scope, idempotencyKey);
            return IdempotencyHelper.Replay(existing);
        }
    }

    entity.Status = TicketStatus.Resolved;
    entity.FinalAnswer = request.FinalAnswer.Trim();
    entity.UpdatedAt = DateTimeOffset.UtcNow;
    db.OutboxMessages.Add(OutboxFactory.Create(SupportEventTypes.TicketResolved, new TicketResolvedPayload { TicketId = id }));
    var dto = TicketMapper.ToDto(entity);
    if (!string.IsNullOrWhiteSpace(idempotencyKey))
    {
        db.IdempotencyRecords.Add(new IdempotencyRecordEntity
        {
            Scope = scope,
            Key = idempotencyKey,
            RequestHash = requestHash,
            ResponseJson = JsonSerializer.Serialize(dto, jsonOptions),
            StatusCode = StatusCodes.Status200OK,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    await db.SaveChangesAsync();

    return Results.Ok(dto);
});

app.MapPost("/tickets/{id}/reopen", async (string id, TicketDbContext db) =>
{
    var entity = await db.Tickets.FindAsync(id);
    if (entity is null) return Results.NotFound();

    entity.Status = TicketStatus.Reopened;
    entity.UpdatedAt = DateTimeOffset.UtcNow;
    db.OutboxMessages.Add(OutboxFactory.Create(SupportEventTypes.TicketUpdated, new { ticketId = id, status = TicketStatus.Reopened }));
    await db.SaveChangesAsync();
    return Results.Ok(TicketMapper.ToDto(entity));
});

app.Run();

public sealed record CreateTicketRequest(string EmployeeId, string Question, string? Category);
public sealed record UpdateTicketRequest(
    string? Status,
    string? AiSuggestedAnswer,
    string? FinalAnswer,
    string? Category,
    IReadOnlyList<RelatedDocument>? RelatedDocuments);
public sealed record ResolveTicketRequest(string? FinalAnswer);
