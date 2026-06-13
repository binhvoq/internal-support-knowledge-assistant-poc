using System.Security.Claims;
using System.Text.Json;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using SupportPoc.Shared.Auth;
using SupportPoc.Shared.Contracts;
using SupportPoc.Shared.Data;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;
using SupportPoc.TicketService.Consumers;
using SupportPoc.TicketService.Data;
using SupportPoc.TicketService.Services;

var builder = WebApplication.CreateBuilder(args);
ProductionSecurityGuard.Validate(builder.Environment, builder.Configuration);

var entraEnabled = builder.Configuration.IsEntraEnabled();
if (entraEnabled)
    builder.Services.AddSupportPocEntraAuth(builder.Configuration);

builder.Services.AddSupportPocMessagingOptions(builder.Configuration);
var ticketsConnectionString = builder.Configuration.GetConnectionString("Tickets")
    ?? DatabaseProvider.DefaultTicketsConnection;
builder.Services.AddDbContext<TicketDbContext>(options =>
    DatabaseProvider.ConfigureDbContext(options, ticketsConnectionString));
builder.Services.AddScoped<ProposeTicketSuggestionApplier>();
builder.Services.AddScoped<AutoSuggestionReconcileService>();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// ---------- MassTransit ----------
var serviceBus = builder.Configuration.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>() ?? new ServiceBusOptions();
var duplicateDetectionWindow = TimeSpan.FromHours(1);

builder.Services.AddMassTransit(mt =>
{
    mt.AddConsumer<ProposeTicketSuggestionConsumer, ProposeTicketSuggestionConsumerDefinition>();

    // Bus outbox: HTTP POST /tickets publish ITicketCreated atomically voi TicketEntity.
    // Consumer outbox + InboxState: duplicate transport delivery theo MessageId (MassTransit 8.x).
    // Idempotency nghiep vu: ProcessedCommands theo CommandId — tach biet voi transport dedup.
    mt.AddEntityFrameworkOutbox<TicketDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
        o.DuplicateDetectionWindow = duplicateDetectionWindow;
    });
    mt.AddEntityFrameworkConsumerOutbox<TicketDbContext>();

    if (serviceBus.Enabled)
    {
        mt.AddSupportPocAzureServiceBusHost(serviceBus, (ctx, cfg) =>
        {
            cfg.UseServiceBusMessageScheduler();
            cfg.ConfigureEndpoints(ctx);
        });
    }
    else
    {
        mt.UsingInMemory((ctx, cfg) =>
        {
            cfg.ConfigureEndpoints(ctx);
        });
    }
});

var app = builder.Build();
app.UseCors();
if (entraEnabled)
    app.UseSupportPocEntraAuth();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TicketDbContext>();
    await DatabaseProvider.EnsureDatabaseReadyAsync(db);
    // IdempotencyRecords + Ticket + MassTransit OutboxState/OutboxMessage/InboxState tables tu sinh.
}

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "ticket-service" }))
    .AllowAnonymous();

app.MapGet("/ready", async (
    Microsoft.Extensions.Options.IOptions<ServiceBusOptions> sbOpts,
    CancellationToken cancellationToken) =>
{
    var pipeline = await MessagingReadinessPolicy.EvaluatePipelineAsync(sbOpts.Value, cancellationToken);
    if (!pipeline.Ready)
        return Results.Json(
            new { ready = false, transport = pipeline.Transport, detail = pipeline.Detail },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    return Results.Ok(new
    {
        ready = true,
        transport = pipeline.Transport,
        detail = pipeline.Detail,
        messaging = new
        {
            busOutbox = true,
            consumerOutbox = true,
            duplicateDetectionWindow = duplicateDetectionWindow.ToString(),
            note = MessagingOutboxDiagnostics.ConsumerOutboxNote,
            businessIdempotency = "ProcessedCommands (CommandId)"
        },
        note = "Chi kiem tra transport/DNS — khong chung minh consumer delivery end-to-end. Dung smoke-test."
    });
}).AllowAnonymous();

app.MapGet("/debug/messaging", async (TicketDbContext db, CancellationToken cancellationToken) =>
    Results.Ok(await MessagingOutboxDiagnostics.BuildSnapshotAsync(
        db, duplicateDetectionWindow, cancellationToken: cancellationToken)))
    .WithDebugOrServicePolicy(entraEnabled, app.Environment);

// /debug/outbox — recent OutboxMessage rows (xem /debug/messaging cho pending summary).
app.MapGet("/debug/outbox", async (TicketDbContext db) =>
{
    var items = await db.Set<OutboxMessage>()
        .OrderByDescending(x => x.SequenceNumber)
        .Take(50)
        .Select(x => new
        {
            x.SequenceNumber,
            x.MessageId,
            x.EnqueueTime,
            x.SentTime,
            x.ContentType,
            x.MessageType
        })
        .ToListAsync();
    return Results.Ok(items);
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapGet("/debug/inbox", async (TicketDbContext db) =>
{
    var items = await db.Set<InboxState>()
        .OrderByDescending(x => x.Received)
        .Take(50)
        .Select(x => new { x.MessageId, x.ConsumerId, x.Received, x.Consumed, x.ReceiveCount })
        .ToListAsync();
    return Results.Ok(items);
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapGet("/debug/idempotency", async (TicketDbContext db) =>
{
    var items = (await db.IdempotencyRecords.ToListAsync())
        .OrderByDescending(x => x.CreatedAt)
        .Take(50)
        .Select(x => new { x.Scope, x.Key, x.RequestHash, x.StatusCode, x.CreatedAt })
        .ToList();
    return Results.Ok(items);
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.MapPost("/tickets", async (
    CreateTicketRequest request,
    HttpContext httpContext,
    TicketDbContext db,
    IPublishEndpoint publish) =>
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
            return IdempotencyHelper.Replay(existing);
        }
    }

    var isService = httpContext.User.IsInRole(AppRoleNames.Service);
    var ownerOid = EntraTicketAccess.GetUserOid(httpContext.User);
    var employeeId = isService
        ? request.EmployeeId.Trim()
        : (httpContext.User.FindFirstValue("preferred_username")
           ?? httpContext.User.Identity?.Name
           ?? request.EmployeeId.Trim());

    var category = string.IsNullOrWhiteSpace(request.Category) ? SupportCategory.Other : request.Category;
    var now = DateTimeOffset.UtcNow;
    var entity = new TicketEntity
    {
        Id = TicketIdGenerator.NewId(),
        EmployeeId = employeeId,
        OwnerOid = isService ? null : ownerOid,
        Category = category,
        Question = request.Question.Trim(),
        Status = TicketStatus.New,
        Version = 1,
        CreatedAt = now,
        UpdatedAt = now
    };

    db.Tickets.Add(entity);

    // Publish event qua outbox - MassTransit se INSERT vao bang OutboxMessage
    // cung transaction voi Ticket. Khi SaveChanges commit, bus outbox relay
    // se pickup va gui len Service Bus. Atomic.
    var jobId = Guid.NewGuid();
    var created = new TicketCreated(
        jobId,
        entity.Id,
        entity.EmployeeId,
        entity.Question,
        entity.Category,
        entity.Version);

    await publish.Publish<ITicketCreated>(created);

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

    return Results.Created($"/tickets/{entity.Id}", dto);
}).WithEntraPolicy(entraEnabled, PolicyNames.UserOrService);

app.MapGet("/tickets", async (string? status, string? category, TicketDbContext db) =>
{
    var query = db.Tickets.AsQueryable();
    if (!string.IsNullOrWhiteSpace(status))
        query = query.Where(t => t.Status == status);
    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(t => t.Category == category);

    var items = (await query.ToListAsync()).OrderByDescending(t => t.CreatedAt).ToList();
    return Results.Ok(items.Select(t => TicketMapper.ToDto(t)));
}).WithEntraPolicy(entraEnabled, PolicyNames.AgentOrService);

app.MapGet("/tickets/mine", async (HttpContext httpContext, TicketDbContext db) =>
{
    var oid = EntraTicketAccess.GetUserOid(httpContext.User);
    if (string.IsNullOrWhiteSpace(oid))
        return Results.Unauthorized();

    var username = httpContext.User.FindFirstValue("preferred_username") ?? httpContext.User.Identity?.Name;
    var items = (await db.Tickets
        .Where(t => t.OwnerOid == oid || (t.OwnerOid == null && username != null && t.EmployeeId == username))
        .ToListAsync()).OrderByDescending(t => t.CreatedAt).ToList();
    return Results.Ok(items.Select(t => TicketMapper.ToDto(t)));
}).WithEntraPolicy(entraEnabled, PolicyNames.EmployeeOrAbove);

app.MapGet("/tickets/{ticketId}/auto-suggestion-reconcile", async (
    string ticketId,
    Guid jobId,
    long? expectedVersion,
    AutoSuggestionReconcileService reconcileService,
    CancellationToken cancellationToken) =>
{
    var result = await reconcileService.ReconcileAsync(ticketId, jobId, expectedVersion, cancellationToken);
    return Results.Ok(result);
}).WithEntraPolicy(entraEnabled, PolicyNames.AgentOrService);

app.MapGet("/tickets/{id}", async (string id, HttpContext httpContext, TicketDbContext db) =>
{
    var entity = await db.Tickets.FindAsync(id);
    if (entity is null)
        return Results.NotFound();
    if (entraEnabled && !EntraTicketAccess.CanReadTicket(entity.OwnerOid, entity.EmployeeId, httpContext.User))
        return Results.Forbid();
    return Results.Ok(TicketMapper.ToDto(entity));
}).WithEntraPolicy(entraEnabled, PolicyNames.UserOrService);

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
            return IdempotencyHelper.Replay(existing);
        }
    }

    if (!TicketLifecycleMutation.TryMutateStatus(entity, TicketStatus.Resolved, request.FinalAnswer, out var mutateError))
        return Results.BadRequest(new { error = mutateError });

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
}).WithEntraPolicy(entraEnabled, PolicyNames.AgentOrService);

app.MapPost("/tickets/{id}/reopen", async (string id, TicketDbContext db) =>
{
    var entity = await db.Tickets.FindAsync(id);
    if (entity is null) return Results.NotFound();

    if (!TicketLifecycleMutation.TryMutateStatus(entity, TicketStatus.Reopened, finalAnswer: null, out var reopenError))
        return Results.BadRequest(new { error = reopenError });

    await db.SaveChangesAsync();
    return Results.Ok(TicketMapper.ToDto(entity));
}).WithEntraPolicy(entraEnabled, PolicyNames.AgentOrService);

app.MapPatch("/tickets/{id}", async (
    string id,
    UpdateTicketLifecycleRequest request,
    HttpContext httpContext,
    TicketDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.Status))
        return Results.BadRequest(new { error = "status la bat buoc." });

    var entity = await db.Tickets.FindAsync(id);
    if (entity is null)
        return Results.NotFound();

    var scope = $"PATCH /tickets/{id}";
    var idempotencyKey = IdempotencyHelper.ReadKey(httpContext);
    var requestHash = IdempotencyHelper.Hash(scope, request);
    if (!string.IsNullOrWhiteSpace(idempotencyKey))
    {
        var existing = await db.IdempotencyRecords.FindAsync([scope, idempotencyKey], httpContext.RequestAborted);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
                return Results.Conflict(new { error = "Idempotency-Key da duoc dung voi payload khac." });
            return IdempotencyHelper.Replay(existing);
        }
    }

    if (!TicketLifecycleMutation.TryMutateStatus(entity, request.Status, request.FinalAnswer, out var mutateError))
        return Results.BadRequest(new { error = mutateError });

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
}).WithEntraPolicy(entraEnabled, PolicyNames.AgentOrService);

app.Run();

public sealed record CreateTicketRequest(string EmployeeId, string Question, string? Category);
public sealed record ResolveTicketRequest(string? FinalAnswer);
public sealed record UpdateTicketLifecycleRequest(string Status, string? FinalAnswer);
