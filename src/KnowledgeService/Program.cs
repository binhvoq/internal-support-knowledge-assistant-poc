using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.KnowledgeService.Data;
using SupportPoc.KnowledgeService.Options;
using SupportPoc.KnowledgeService.Search;
using SupportPoc.KnowledgeService.Services;
using SupportPoc.Shared.Auth;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

var entraEnabled = builder.Configuration.IsEntraEnabled();
if (entraEnabled)
    builder.Services.AddSupportPocEntraAuth(builder.Configuration);

builder.Services.Configure<AzureSearchOptions>(builder.Configuration.GetSection(AzureSearchOptions.SectionName));
builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<AzureStorageOptions>(builder.Configuration.GetSection(AzureStorageOptions.SectionName));
builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.AddSingleton<ReindexState>();
builder.Services.AddSingleton<KnowledgeSearchService>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<DocumentBlobStore>();
builder.Services.AddDbContext<KnowledgeDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Knowledge") ?? "Data Source=knowledge.db"));
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// ---------- MassTransit (publish-only - KnowledgeService khong tham gia saga) ----------
var serviceBus = builder.Configuration.GetSection(ServiceBusOptions.SectionName).Get<ServiceBusOptions>() ?? new ServiceBusOptions();
builder.Services.AddMassTransit(mt =>
{
    if (serviceBus.Enabled)
    {
        mt.UsingAzureServiceBus((ctx, cfg) =>
        {
            cfg.Host(serviceBus.ConnectionString);
            cfg.ConfigureEndpoints(ctx);
        });
    }
    else
    {
        mt.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
    }
});

var app = builder.Build();
app.UseCors();
if (entraEnabled)
    app.UseSupportPocEntraAuth();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
    await db.Database.EnsureCreatedAsync();
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
    if (!await db.Documents.AnyAsync())
    {
        db.Documents.AddRange(SeedData.Documents);
        await db.SaveChangesAsync();
    }

    var search = scope.ServiceProvider.GetRequiredService<KnowledgeSearchService>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        await search.EnsureIndexAsync();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Khong khoi tao duoc Azure AI Search index; service se chay voi local search fallback.");
    }
}

static KnowledgeDocumentDto ToDto(KnowledgeDocumentEntity e) => new()
{
    Id = e.Id,
    Title = e.Title,
    Category = e.Category,
    Content = e.Content,
    SourceUrl = e.SourceUrl,
    UpdatedAt = e.UpdatedAt
};

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "knowledge-service" }))
    .AllowAnonymous();

app.MapGet("/documents", async (KnowledgeDbContext db) =>
{
    var docs = await db.Documents.OrderBy(d => d.Id).ToListAsync();
    return Results.Ok(docs.Select(ToDto));
}).WithEntraPolicy(entraEnabled, PolicyNames.UserOrService);

app.MapPost("/documents", async (
    CreateDocumentRequest request,
    KnowledgeDbContext db,
    DocumentBlobStore blobStore,
    IPublishEndpoint publish,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "title va content la bat buoc." });

    var ids = await db.Documents.Select(d => d.Id).ToListAsync();
    var entity = new KnowledgeDocumentEntity
    {
        Id = DocumentIdGenerator.Next(ids),
        Title = request.Title.Trim(),
        Category = string.IsNullOrWhiteSpace(request.Category) ? SupportCategory.Other : request.Category,
        Content = request.Content.Trim(),
        SourceUrl = request.SourceUrl,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    var blobUrl = await blobStore.UploadAsync(entity.Id, entity.Content, cancellationToken);
    if (string.IsNullOrWhiteSpace(entity.SourceUrl) && blobUrl is not null)
        entity.SourceUrl = blobUrl;

    db.Documents.Add(entity);
    await db.SaveChangesAsync();
    try
    {
        await publish.Publish(new KnowledgeDocumentUploaded(entity.Id), cancellationToken);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Publish KnowledgeDocumentUploaded that bai; tiep tuc local flow.");
    }
    return Results.Created($"/documents/{entity.Id}", ToDto(entity));
}).WithEntraPolicy(entraEnabled, PolicyNames.KnowledgeAdmin);

app.MapGet("/documents/reindex-status", (ReindexState state) => Results.Ok(state.Snapshot()))
    .WithEntraPolicy(entraEnabled, PolicyNames.KnowledgeAdmin);

app.MapPost("/documents/reindex", async (
    HttpContext httpContext,
    KnowledgeDbContext db,
    KnowledgeSearchService search,
    EmbeddingService embeddings,
    ReindexState state,
    IPublishEndpoint publish) =>
{
    const string scope = "POST /documents/reindex";
    var idempotencyKey = ReadIdempotencyKey(httpContext);
    var requestHash = HashIdempotency(scope, "reindex");
    if (!string.IsNullOrWhiteSpace(idempotencyKey))
    {
        var existing = await db.IdempotencyRecords.FindAsync([scope, idempotencyKey], httpContext.RequestAborted);
        if (existing is not null)
        {
            if (existing.RequestHash != requestHash)
                return Results.Conflict(new { error = "Idempotency-Key da duoc dung voi payload khac." });
            app.Logger.LogInformation("Idempotency replay {Scope} Key={Key}", scope, idempotencyKey);
            return Results.Text(existing.ResponseJson, "application/json", statusCode: existing.StatusCode);
        }
    }

    if (state.Status == "Indexing")
        return Results.Conflict(new { error = "Re-index dang chay." });

    state.Set("Indexing");
    var docs = await db.Documents.ToListAsync();
    try
    {
        await search.EnsureIndexAsync();
        var batch = new List<(KnowledgeDocumentEntity Entity, IReadOnlyList<float>? Embedding)>();
        foreach (var doc in docs)
        {
            var vector = await embeddings.CreateEmbeddingAsync($"{doc.Title}\n{doc.Content}");
            batch.Add((doc, vector));
        }
        await search.UpsertDocumentsAsync(batch);
        state.Set("Completed");
        try
        {
            await publish.Publish(new KnowledgeIndexUpdated(docs.Count));
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Publish KnowledgeIndexUpdated that bai; tiep tuc local flow.");
        }
        var response = new { status = "Completed", documentCount = docs.Count };
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            db.IdempotencyRecords.Add(new IdempotencyRecordEntity
            {
                Scope = scope,
                Key = idempotencyKey,
                RequestHash = requestHash,
                ResponseJson = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                StatusCode = StatusCodes.Status200OK,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        state.Set("Failed", ex.Message);
        return Results.Problem(detail: ex.Message, title: "Re-index that bai");
    }
}).WithEntraPolicy(entraEnabled, PolicyNames.KnowledgeAdmin);

app.MapGet("/search", async (
    string query,
    string? category,
    string? mode,
    IOptions<AzureSearchOptions> searchOptions,
    KnowledgeSearchService search,
    EmbeddingService embeddings,
    KnowledgeDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "query la bat buoc." });

    var normalizedMode = (mode ?? "hybrid").ToLowerInvariant();
    var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (!searchOptions.Value.Enabled)
    {
        var docsQuery = db.Documents.AsQueryable();
        if (!string.IsNullOrWhiteSpace(category))
            docsQuery = docsQuery.Where(d => d.Category == category);

        var docs = await docsQuery.ToListAsync(cancellationToken);
        var localResults = SearchLocal(docs, query, terms);

        return Results.Ok(new { query, mode = "local-keyword", results = localResults });
    }

    IReadOnlyList<RelatedDocument> hits;
    try
    {
        var embedding = await embeddings.CreateEmbeddingAsync(query, cancellationToken);
        hits = normalizedMode switch
        {
            "vector" when embedding is { Count: > 0 } => await search.VectorSearchAsync(embedding, category, cancellationToken: cancellationToken),
            "keyword" => await search.SearchAsync(query, category, cancellationToken: cancellationToken),
            _ when embedding is { Count: > 0 } => await search.HybridSearchAsync(query, embedding, category, cancellationToken: cancellationToken),
            _ => await search.SearchAsync(query, category, cancellationToken: cancellationToken)
        };
    }
    catch
    {
        var docsQuery = db.Documents.AsQueryable();
        if (!string.IsNullOrWhiteSpace(category))
            docsQuery = docsQuery.Where(d => d.Category == category);
        var docs = await docsQuery.ToListAsync(cancellationToken);
        hits = SearchLocal(docs, query, terms);
        normalizedMode = "local-keyword-fallback";
    }

    var results = await SearchResultEnricher.WithContentAsync(db, hits, cancellationToken);

    return Results.Ok(new { query, mode = normalizedMode, results });
}).WithEntraPolicy(entraEnabled, PolicyNames.UserOrService);

app.MapGet("/categories", () => Results.Ok(SupportCategory.All))
    .WithEntraPolicy(entraEnabled, PolicyNames.UserOrService);

app.MapGet("/debug/idempotency", async (KnowledgeDbContext db) =>
{
    var items = (await db.IdempotencyRecords
        .ToListAsync())
        .OrderByDescending(x => x.CreatedAt)
        .Take(50)
        .Select(x => new { x.Scope, x.Key, x.RequestHash, x.StatusCode, x.CreatedAt })
        .ToList();
    return Results.Ok(items);
}).AllowAnonymous();

app.Run();

static double ScoreLocalHit(KnowledgeDocumentEntity doc, string query, IReadOnlyList<string> terms)
{
    var title = doc.Title ?? "";
    var content = doc.Content ?? "";
    var haystack = $"{title}\n{content}";
    var score = 0d;

    if (haystack.Contains(query, StringComparison.OrdinalIgnoreCase))
        score += 1;

    foreach (var term in terms)
    {
        if (title.Contains(term, StringComparison.OrdinalIgnoreCase))
            score += 0.4;
        if (content.Contains(term, StringComparison.OrdinalIgnoreCase))
            score += 0.2;
    }

    return Math.Min(score, 1);
}

static IReadOnlyList<RelatedDocument> SearchLocal(
    IReadOnlyList<KnowledgeDocumentEntity> docs,
    string query,
    IReadOnlyList<string> terms) =>
    docs
        .Select(doc => new RelatedDocument
        {
            DocumentId = doc.Id,
            Title = doc.Title,
            Content = doc.Content,
            Score = ScoreLocalHit(doc, query, terms)
        })
        .Where(hit => hit.Score > 0)
        .OrderByDescending(hit => hit.Score)
        .Take(5)
        .ToList();

static string? ReadIdempotencyKey(HttpContext httpContext) =>
    httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var values)
        ? values.FirstOrDefault()
        : null;

static string HashIdempotency(string scope, string payload)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{payload}"));
    return Convert.ToHexString(bytes);
}

public sealed record CreateDocumentRequest(string Title, string Category, string Content, string? SourceUrl);

// Knowledge-domain events - khong tham gia saga, chi broadcast cho subscriber khac (vd. analytics).
public sealed record KnowledgeDocumentUploaded(string DocumentId);
public sealed record KnowledgeIndexUpdated(int DocumentCount);
