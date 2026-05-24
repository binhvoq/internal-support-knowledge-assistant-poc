using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SupportPoc.KnowledgeService.Data;
using SupportPoc.KnowledgeService.Options;
using SupportPoc.KnowledgeService.Search;
using SupportPoc.KnowledgeService.Services;
using SupportPoc.Shared.Events;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AzureSearchOptions>(builder.Configuration.GetSection(AzureSearchOptions.SectionName));
builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<AzureStorageOptions>(builder.Configuration.GetSection(AzureStorageOptions.SectionName));
builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.AddSingleton<ISupportEventPublisher, ServiceBusEventPublisher>();
builder.Services.AddSingleton<ReindexState>();
builder.Services.AddSingleton<KnowledgeSearchService>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<DocumentBlobStore>();
builder.Services.AddDbContext<KnowledgeDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Knowledge") ?? "Data Source=knowledge.db"));
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KnowledgeDbContext>();
    await db.Database.EnsureCreatedAsync();
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

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "knowledge-service" }));

app.MapGet("/documents", async (KnowledgeDbContext db) =>
{
    var docs = await db.Documents.OrderBy(d => d.Id).ToListAsync();
    return Results.Ok(docs.Select(ToDto));
});

app.MapPost("/documents", async (
    CreateDocumentRequest request,
    KnowledgeDbContext db,
    DocumentBlobStore blobStore,
    ISupportEventPublisher publisher,
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
    await TryPublishAsync(app.Logger, publisher, SupportEventTypes.KnowledgeDocumentUploaded, new { documentId = entity.Id }, cancellationToken);
    return Results.Created($"/documents/{entity.Id}", ToDto(entity));
});

app.MapGet("/documents/reindex-status", (ReindexState state) => Results.Ok(state.Snapshot()));

app.MapPost("/documents/reindex", async (
    KnowledgeDbContext db,
    KnowledgeSearchService search,
    EmbeddingService embeddings,
    ReindexState state,
    ISupportEventPublisher publisher) =>
{
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
        await TryPublishAsync(app.Logger, publisher, SupportEventTypes.KnowledgeIndexUpdated, new { documentCount = docs.Count });
        return Results.Ok(new { status = "Completed", documentCount = docs.Count });
    }
    catch (Exception ex)
    {
        state.Set("Failed", ex.Message);
        return Results.Problem(detail: ex.Message, title: "Re-index that bai");
    }
});

app.MapGet("/search", async (
    string query,
    string? category,
    string mode,
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
});

app.MapGet("/categories", () => Results.Ok(SupportCategory.All));

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

public sealed record CreateDocumentRequest(string Title, string Category, string Content, string? SourceUrl);
