using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SupportPoc.KnowledgeService.Data;
using SupportPoc.KnowledgeService.Options;
using SupportPoc.KnowledgeService.Search;
using SupportPoc.KnowledgeService.Services;
using SupportPoc.Shared.Auth;
using SupportPoc.Shared.Data;
using SupportPoc.Shared.Messaging;
using SupportPoc.Shared.Models;

var builder = WebApplication.CreateBuilder(args);
ProductionSecurityGuard.Validate(builder.Environment, builder.Configuration);

var entraEnabled = builder.Configuration.IsEntraEnabled();
if (entraEnabled)
    builder.Services.AddSupportPocEntraAuth(builder.Configuration);

builder.Services.Configure<AzureSearchOptions>(builder.Configuration.GetSection(AzureSearchOptions.SectionName));
builder.Services.Configure<AzureOpenAIOptions>(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));
builder.Services.Configure<AzureStorageOptions>(builder.Configuration.GetSection(AzureStorageOptions.SectionName));
builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.AddSingleton<ReindexState>();
builder.Services.AddSingleton<KnowledgeSearchService>();
builder.Services.AddHttpClient(nameof(AzureSearchIngestionService));
builder.Services.AddSingleton<AzureSearchIngestionService>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<DocumentBlobStore>();
builder.Services.AddSingleton<DocumentIngestionStatusRefresher>();
builder.Services.AddHostedService<DocumentIngestionRefreshBackgroundService>();
var knowledgeConnectionString = builder.Configuration.GetConnectionString("Knowledge")
    ?? DatabaseProvider.DefaultKnowledgeConnection;
builder.Services.AddDbContext<KnowledgeDbContext>(options =>
    DatabaseProvider.ConfigureDbContext(options, knowledgeConnectionString));
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

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
    await DatabaseProvider.EnsureDatabaseReadyAsync(db);
    if (!await db.Documents.AnyAsync())
    {
        db.Documents.AddRange(SeedData.Documents);
        await db.SaveChangesAsync();
    }

    var search = scope.ServiceProvider.GetRequiredService<KnowledgeSearchService>();
    var ingestion = scope.ServiceProvider.GetRequiredService<AzureSearchIngestionService>();
    var blobStore = scope.ServiceProvider.GetRequiredService<DocumentBlobStore>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        await search.EnsureChunkIndexAsync();
        await ingestion.EnsurePipelineAsync();
        var searchOptions = scope.ServiceProvider.GetRequiredService<IOptions<AzureSearchOptions>>().Value;
        var ingestionRefresher = scope.ServiceProvider.GetRequiredService<DocumentIngestionStatusRefresher>();
        await SeedDemoPdfAsync(app.Environment, db, blobStore, ingestion, ingestionRefresher, searchOptions, logger);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Khong khoi tao duoc Azure AI Search chunk pipeline; service se chay voi local search fallback.");
    }
}

static KnowledgeDocumentDto ToDto(KnowledgeDocumentEntity e) => new()
{
    Id = e.Id,
    Title = e.Title,
    Category = e.Category,
    Content = e.Content,
    SourceUrl = e.SourceUrl,
    FileName = e.FileName,
    ContentType = e.ContentType,
    IngestionStatus = e.IngestionStatus,
    IngestionMessage = e.IngestionMessage,
    IngestedAt = e.IngestedAt,
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
    AzureSearchIngestionService ingestion,
    DocumentIngestionStatusRefresher ingestionRefresher,
    IOptions<AzureSearchOptions> searchOptions,
    IPublishEndpoint publish,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
        return Results.BadRequest(new { error = "title va content la bat buoc." });

    var ids = await db.Documents.Select(d => d.Id).ToListAsync();
    var uploadedAt = DateTimeOffset.UtcNow;
    var entity = new KnowledgeDocumentEntity
    {
        Id = DocumentIdGenerator.Next(ids),
        Title = request.Title.Trim(),
        Category = string.IsNullOrWhiteSpace(request.Category) ? SupportCategory.Other : request.Category,
        Content = request.Content.Trim(),
        SourceUrl = request.SourceUrl,
        ContentType = "text/plain",
        IngestionStatus = "Processing",
        IngestionMessage = "Dang upload text va cho Azure indexer chunk/embed.",
        UpdatedAt = uploadedAt
    };
    entity.FileName = $"{entity.Id}.txt";

    if (!ingestion.IsPipelineConfigured)
    {
        entity.IngestionStatus = "Ready";
        entity.IngestionMessage = "Azure pipeline chua cau hinh; dung local keyword search tren noi dung text.";
        entity.IngestedAt = uploadedAt;
        db.Documents.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/documents/{entity.Id}", ToDto(entity));
    }

    string? blobUrl;
    try
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(entity.Content));
        blobUrl = await blobStore.UploadKnowledgeFileAsync(
            entity.Id,
            stream,
            entity.FileName,
            "text/plain",
            BuildBlobMetadata(entity, uploadedAt),
            cancellationToken);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Upload knowledge text blob for {DocumentId} failed.", entity.Id);
        return Results.Problem(
            title: "Upload knowledge file len Blob that bai.",
            detail: "Khong the upload file len Azure Blob Storage luc nay. Thu lai sau.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (blobUrl is not null)
        entity.SourceUrl = blobUrl;

    db.Documents.Add(entity);
    await db.SaveChangesAsync(cancellationToken);
    await WaitForChunkIndexingAsync(entity, db, ingestion, ingestionRefresher, searchOptions.Value, cancellationToken);
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

app.MapPost("/documents/upload-pdf", async (
    [FromForm] IFormFile file,
    [FromForm] string? title,
    [FromForm] string? category,
    KnowledgeDbContext db,
    DocumentBlobStore blobStore,
    AzureSearchIngestionService ingestion,
    DocumentIngestionStatusRefresher ingestionRefresher,
    IOptions<AzureSearchOptions> searchOptions,
    IPublishEndpoint publish,
    CancellationToken cancellationToken) =>
{
    if (file.Length <= 0)
        return Results.BadRequest(new { error = "PDF khong duoc de trong." });
    if (file.Length > 20 * 1024 * 1024)
        return Results.BadRequest(new { error = "PDF toi da 20 MB." });
    if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Chi ho tro upload file PDF." });

    if (!ingestion.IsPipelineConfigured)
        return Results.BadRequest(new { error = "Azure Search indexer pipeline chua cau hinh (Search + Storage + OpenAI)." });

    var ids = await db.Documents.Select(d => d.Id).ToListAsync(cancellationToken);
    var uploadedAt = DateTimeOffset.UtcNow;
    var normalizedTitle = string.IsNullOrWhiteSpace(title)
        ? Path.GetFileNameWithoutExtension(file.FileName)
        : title.Trim();
    var fileName = Path.GetFileName(file.FileName);
    var entity = new KnowledgeDocumentEntity
    {
        Id = DocumentIdGenerator.Next(ids),
        Title = normalizedTitle,
        Category = string.IsNullOrWhiteSpace(category) ? SupportCategory.Other : category.Trim(),
        Content = string.Empty,
        FileName = fileName,
        ContentType = NormalizeKnowledgeContentType(fileName, file.ContentType),
        IngestionStatus = "Processing",
        IngestionMessage = "Da upload PDF len Blob, dang cho Azure indexer chunk/embed.",
        UpdatedAt = uploadedAt
    };

    string? blobUrl;
    try
    {
        await using var stream = file.OpenReadStream();
        blobUrl = await blobStore.UploadKnowledgeFileAsync(
            entity.Id,
            stream,
            fileName,
            entity.ContentType,
            BuildBlobMetadata(entity, uploadedAt),
            cancellationToken);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Upload PDF blob for {DocumentId} failed.", entity.Id);
        return Results.Problem(
            title: "Upload PDF len Blob that bai.",
            detail: "Khong the upload file len Azure Blob Storage luc nay. Thu lai sau.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (blobUrl is not null)
        entity.SourceUrl = blobUrl;

    db.Documents.Add(entity);
    await db.SaveChangesAsync(cancellationToken);
    await WaitForChunkIndexingAsync(entity, db, ingestion, ingestionRefresher, searchOptions.Value, cancellationToken);
    try
    {
        await publish.Publish(new KnowledgeDocumentUploaded(entity.Id), cancellationToken);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Publish KnowledgeDocumentUploaded that bai; tiep tuc local flow.");
    }

    return Results.Created($"/documents/{entity.Id}", ToDto(entity));
})
.DisableAntiforgery()
.WithEntraPolicy(entraEnabled, PolicyNames.KnowledgeAdmin);

app.MapDelete("/documents/{id}", async (
    string id,
    KnowledgeDbContext db,
    DocumentBlobStore blobStore,
    KnowledgeSearchService search,
    CancellationToken cancellationToken) =>
{
    var entity = await db.Documents.FindAsync([id], cancellationToken);
    if (entity is null)
        return Results.NotFound(new { error = $"Document {id} khong ton tai." });

    try
    {
        await search.DeleteChunksByDocumentIdAsync(id, cancellationToken);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Xoa chunk cua document {DocumentId} khoi Azure AI Search that bai.", id);
        return Results.Problem(detail: ex.Message, title: $"Xoa chunk cua document {id} khoi Azure AI Search that bai");
    }

    await blobStore.DeleteAsync(id, entity.FileName, cancellationToken);
    db.Documents.Remove(entity);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new { status = "Deleted", documentId = id });
}).WithEntraPolicy(entraEnabled, PolicyNames.KnowledgeAdmin);

app.MapGet("/documents/reindex-status", (ReindexState state) => Results.Ok(state.Snapshot()))
    .WithEntraPolicy(entraEnabled, PolicyNames.KnowledgeAdmin);

app.MapPost("/documents/reindex", async (
    HttpContext httpContext,
    KnowledgeDbContext db,
    AzureSearchIngestionService ingestion,
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

    if (!ingestion.IsPipelineConfigured)
        return Results.BadRequest(new { error = "Azure Search indexer pipeline chua cau hinh." });

    state.Set("Indexing");
    var docs = await db.Documents.ToListAsync();
    try
    {
        await ingestion.EnsurePipelineAsync(httpContext.RequestAborted);
        await ingestion.TryRunIndexerAsync(httpContext.RequestAborted);
        state.Set("Completed");
        try
        {
            await publish.Publish(new KnowledgeIndexUpdated(docs.Count));
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Publish KnowledgeIndexUpdated that bai; tiep tuc local flow.");
        }

        var response = new { status = "Completed", documentCount = docs.Count, mode = "azure-indexer" };
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
    string? rerank,
    IOptions<AzureSearchOptions> searchOptions,
    KnowledgeSearchService search,
    EmbeddingService embeddings,
    KnowledgeDbContext db,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(query))
        return Results.BadRequest(new { error = "query la bat buoc." });

    if (!TryParseSearchMode(mode, out var searchMode))
        return Results.BadRequest(new { error = "mode chi ho tro: keyword, vector, hybrid." });

    if (!TryParseRerankMethod(rerank, out var rerankMethod))
        return Results.BadRequest(new { error = "rerank chi ho tro: none, mmr, semantic." });

    if (searchMode == KnowledgeSearchMode.Vector && rerankMethod == KnowledgeRerankMethod.AzureSemantic)
        return Results.BadRequest(new { error = "rerank=semantic khong ho tro mode=vector; dung mode=keyword hoac mode=hybrid." });

    if (rerankMethod == KnowledgeRerankMethod.AzureSemantic && !searchOptions.Value.SemanticRankerEnabled)
        return Results.BadRequest(new { error = "Azure Semantic Ranker chua duoc enable trong config." });

    var normalizedMode = FormatSearchMode(searchMode);
    var normalizedRerank = FormatRerankMethod(rerankMethod);
    var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (!searchOptions.Value.Enabled)
    {
        var docsQuery = db.Documents.AsQueryable();
        if (!string.IsNullOrWhiteSpace(category))
            docsQuery = docsQuery.Where(d => d.Category == category);

        var docs = await docsQuery.ToListAsync(cancellationToken);
        var localResults = SearchLocal(docs, query, terms);
        return Results.Ok(new { query, mode = "local-keyword", rerank = "none", results = localResults });
    }

    IReadOnlyList<RelatedDocument> hits;
    try
    {
        var needsEmbedding = searchMode != KnowledgeSearchMode.Keyword || rerankMethod == KnowledgeRerankMethod.Mmr;
        var embedding = needsEmbedding
            ? await embeddings.CreateEmbeddingAsync(query, cancellationToken)
            : null;

        if (needsEmbedding && embedding is not { Count: > 0 })
        {
            hits = await search.SearchAsync(
                query,
                null,
                category,
                KnowledgeSearchMode.Keyword,
                KnowledgeRerankMethod.None,
                cancellationToken: cancellationToken);
            normalizedMode = "keyword";
            normalizedRerank = "none";
        }
        else
        {
            hits = await search.SearchAsync(
                query,
                embedding,
                category,
                searchMode,
                rerankMethod,
                cancellationToken: cancellationToken);
        }
    }
    catch
    {
        var docsQuery = db.Documents.AsQueryable();
        if (!string.IsNullOrWhiteSpace(category))
            docsQuery = docsQuery.Where(d => d.Category == category);
        var docs = await docsQuery.ToListAsync(cancellationToken);
        hits = SearchLocal(docs, query, terms);
        normalizedMode = "local-keyword-fallback";
        normalizedRerank = "none";
    }

    return Results.Ok(new { query, mode = normalizedMode, rerank = normalizedRerank, index = searchOptions.Value.IndexName, results = hits });
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
}).WithDebugOrServicePolicy(entraEnabled, app.Environment);

app.Run();

static bool TryParseSearchMode(string? raw, out KnowledgeSearchMode mode)
{
    mode = KnowledgeSearchMode.Hybrid;
    if (string.IsNullOrWhiteSpace(raw))
        return true;

    switch (raw.Trim().ToLowerInvariant())
    {
        case "keyword":
            mode = KnowledgeSearchMode.Keyword;
            return true;
        case "vector":
            mode = KnowledgeSearchMode.Vector;
            return true;
        case "hybrid":
            mode = KnowledgeSearchMode.Hybrid;
            return true;
        default:
            return false;
    }
}

static bool TryParseRerankMethod(string? raw, out KnowledgeRerankMethod rerankMethod)
{
    // Default RAG context retrieval favors relevance; MMR remains opt-in for diversity-oriented search.
    rerankMethod = KnowledgeRerankMethod.None;

    if (string.IsNullOrWhiteSpace(raw))
        return true;

    switch (raw.Trim().ToLowerInvariant())
    {
        case "none":
        case "off":
            rerankMethod = KnowledgeRerankMethod.None;
            return true;
        case "mmr":
            rerankMethod = KnowledgeRerankMethod.Mmr;
            return true;
        case "semantic":
        case "azuresemantic":
        case "azure-semantic":
            rerankMethod = KnowledgeRerankMethod.AzureSemantic;
            return true;
        default:
            return false;
    }
}

static string FormatSearchMode(KnowledgeSearchMode mode) =>
    mode switch
    {
        KnowledgeSearchMode.Keyword => "keyword",
        KnowledgeSearchMode.Vector => "vector",
        KnowledgeSearchMode.Hybrid => "hybrid",
        _ => mode.ToString()
    };

static string FormatRerankMethod(KnowledgeRerankMethod rerankMethod) =>
    rerankMethod switch
    {
        KnowledgeRerankMethod.None => "none",
        KnowledgeRerankMethod.Mmr => "mmr",
        KnowledgeRerankMethod.AzureSemantic => "semantic",
        _ => rerankMethod.ToString()
    };

static IReadOnlyDictionary<string, string> BuildBlobMetadata(KnowledgeDocumentEntity entity, DateTimeOffset uploadedAt) =>
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["documentid"] = entity.Id,
        ["title"] = entity.Title,
        ["category"] = entity.Category,
        ["filename"] = entity.FileName ?? $"{entity.Id}.bin",
        ["uploadedat"] = uploadedAt.ToString("O")
    };

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
            Score = ScoreLocalHit(doc, query, terms),
            FileName = doc.FileName
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

static async Task WaitForChunkIndexingAsync(
    KnowledgeDocumentEntity entity,
    KnowledgeDbContext db,
    AzureSearchIngestionService ingestion,
    DocumentIngestionStatusRefresher refresher,
    AzureSearchOptions searchOptions,
    CancellationToken cancellationToken)
{
    try
    {
        await ingestion.EnsurePipelineAsync(cancellationToken);
        await ingestion.TryRunIndexerAsync(cancellationToken);

        var timeout = TimeSpan.FromSeconds(Math.Max(15, searchOptions.IngestionPollTimeoutSeconds));
        var deadline = DateTimeOffset.UtcNow + timeout;
        IngestionPollDecision? finalDecision = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var decision = await refresher.EvaluateDocumentAsync(entity, pollTimedOut: false, cancellationToken);
            if (decision.Action == IngestionPollAction.Failed)
                throw new InvalidOperationException(decision.Message);

            if (decision.Action == IngestionPollAction.Ready)
            {
                finalDecision = decision;
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        finalDecision ??= await refresher.EvaluateDocumentAsync(entity, pollTimedOut: true, cancellationToken);
        if (finalDecision.Action == IngestionPollAction.Failed)
            throw new InvalidOperationException(finalDecision.Message);

        DocumentIngestionStatusRefresher.ApplyDecision(entity, finalDecision, searchOptions.IndexName);
    }
    catch (Exception ex)
    {
        entity.IngestionStatus = "Failed";
        entity.IngestionMessage = ex.Message;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
    }

    await db.SaveChangesAsync(cancellationToken);
}

static async Task SeedDemoPdfAsync(
    IWebHostEnvironment environment,
    KnowledgeDbContext db,
    DocumentBlobStore blobStore,
    AzureSearchIngestionService ingestion,
    DocumentIngestionStatusRefresher ingestionRefresher,
    AzureSearchOptions searchOptions,
    ILogger logger)
{
    if (!ingestion.IsPipelineConfigured)
        return;

    const string fileName = "long-employee-policy-handbook.pdf";
    var pdfPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "test-pdfs", fileName));
    if (!File.Exists(pdfPath))
        return;

    var existing = await db.Documents
        .FirstOrDefaultAsync(d => d.FileName == fileName || d.Title == "Long Employee Policy Handbook Demo Seed");
    if (existing is not null)
    {
        if (!string.Equals(existing.IngestionStatus, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            var decision = await ingestionRefresher.EvaluateDocumentAsync(existing, pollTimedOut: true);
            DocumentIngestionStatusRefresher.ApplyDecision(existing, decision, searchOptions.IndexName);
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Da refresh demo seed PDF {FileName}: {Status} - {Message}",
                fileName,
                existing.IngestionStatus,
                existing.IngestionMessage);
        }

        return;
    }

    var ids = await db.Documents.Select(d => d.Id).ToListAsync();
    var uploadedAt = DateTimeOffset.UtcNow;
    var entity = new KnowledgeDocumentEntity
    {
        Id = DocumentIdGenerator.Next(ids),
        Title = "Long Employee Policy Handbook Demo Seed",
        Category = SupportCategory.HR,
        Content = string.Empty,
        FileName = fileName,
        ContentType = "application/pdf",
        IngestionStatus = "Processing",
        IngestionMessage = "Demo seed PDF dang duoc dua vao Azure indexer pipeline.",
        UpdatedAt = uploadedAt
    };

    await using (var stream = File.OpenRead(pdfPath))
    {
        var blobUrl = await blobStore.UploadKnowledgeFileAsync(
            entity.Id,
            stream,
            fileName,
            "application/pdf",
            BuildBlobMetadata(entity, uploadedAt));
        if (blobUrl is not null)
            entity.SourceUrl = blobUrl;
    }

    db.Documents.Add(entity);
    await db.SaveChangesAsync();
    await WaitForChunkIndexingAsync(entity, db, ingestion, ingestionRefresher, searchOptions, CancellationToken.None);
    logger.LogInformation("Da auto-seed demo PDF {FileName} thanh {DocumentId}.", fileName, entity.Id);
}

static string NormalizeKnowledgeContentType(string fileName, string? contentType)
{
    if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(contentType)
            || string.Equals(contentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)))
    {
        return "application/pdf";
    }

    return string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
}

public sealed record CreateDocumentRequest(string Title, string Category, string Content, string? SourceUrl);

public sealed record KnowledgeDocumentUploaded(string DocumentId);
public sealed record KnowledgeIndexUpdated(int DocumentCount);
