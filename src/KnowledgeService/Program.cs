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
builder.Services.AddSingleton<PdfKnowledgeExtractor>();
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
    await EnsureDocumentColumnsAsync(db);
    if (!await db.Documents.AnyAsync())
    {
        db.Documents.AddRange(SeedData.Documents);
        await db.SaveChangesAsync();
    }

    var search = scope.ServiceProvider.GetRequiredService<KnowledgeSearchService>();
    var embeddings = scope.ServiceProvider.GetRequiredService<EmbeddingService>();
    var blobStore = scope.ServiceProvider.GetRequiredService<DocumentBlobStore>();
    var extractor = scope.ServiceProvider.GetRequiredService<PdfKnowledgeExtractor>();
    var searchOptions = scope.ServiceProvider.GetRequiredService<IOptions<AzureSearchOptions>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    try
    {
        await search.EnsureIndexAsync();
        await SeedDemoPdfAsync(app.Environment, db, extractor, blobStore, search, embeddings, searchOptions.Value, logger);
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
    KnowledgeSearchService search,
    EmbeddingService embeddings,
    IOptions<AzureSearchOptions> searchOptions,
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
        ContentType = "text/plain",
        IngestionStatus = "Processing",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    var blobUrl = await blobStore.UploadAsync(entity.Id, entity.Content, cancellationToken);
    if (string.IsNullOrWhiteSpace(entity.SourceUrl) && blobUrl is not null)
        entity.SourceUrl = blobUrl;

    db.Documents.Add(entity);
    await db.SaveChangesAsync();
    await MarkDocumentReadyAsync(entity, db, search, embeddings, searchOptions.Value, cancellationToken);
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
    PdfKnowledgeExtractor extractor,
    DocumentBlobStore blobStore,
    KnowledgeSearchService search,
    EmbeddingService embeddings,
    IOptions<AzureSearchOptions> searchOptions,
    IPublishEndpoint publish,
    CancellationToken cancellationToken) =>
{
    if (file.Length <= 0)
        return Results.BadRequest(new { error = "PDF khong duoc de trong." });
    if (file.Length > 20 * 1024 * 1024)
        return Results.BadRequest(new { error = "PDF toi da 20 MB cho POC." });
    if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(file.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Chi ho tro upload file PDF." });

    string content;
    try
    {
        await using var stream = file.OpenReadStream();
        content = extractor.ExtractText(stream);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Khong doc duoc PDF: {ex.Message}" });
    }

    if (string.IsNullOrWhiteSpace(content))
        return Results.BadRequest(new { error = "PDF khong co text de trich xuat. Neu la PDF scan/image, can them OCR truoc khi RAG." });

    var ids = await db.Documents.Select(d => d.Id).ToListAsync(cancellationToken);
    var normalizedTitle = string.IsNullOrWhiteSpace(title)
        ? Path.GetFileNameWithoutExtension(file.FileName)
        : title.Trim();
    var entity = new KnowledgeDocumentEntity
    {
        Id = DocumentIdGenerator.Next(ids),
        Title = normalizedTitle,
        Category = string.IsNullOrWhiteSpace(category) ? SupportCategory.Other : category.Trim(),
        Content = content,
        FileName = Path.GetFileName(file.FileName),
        ContentType = file.ContentType,
        IngestionStatus = "Processing",
        IngestionMessage = "Da trich xuat text tu PDF, dang dua vao knowledge index.",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    var blobUrl = await blobStore.UploadAsync(entity.Id, entity.Content, cancellationToken);
    if (blobUrl is not null)
        entity.SourceUrl = blobUrl;

    db.Documents.Add(entity);
    await db.SaveChangesAsync(cancellationToken);
    await MarkDocumentReadyAsync(entity, db, search, embeddings, searchOptions.Value, cancellationToken);
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
        await search.DeleteDocumentAsync(id, cancellationToken);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Xoa document {DocumentId} khoi Azure AI Search that bai.", id);
        return Results.Problem(detail: ex.Message, title: $"Xoa document {id} khoi Azure AI Search that bai");
    }

    await blobStore.DeleteAsync(id, cancellationToken);
    db.Documents.Remove(entity);
    await db.SaveChangesAsync(cancellationToken);

    return Results.Ok(new { status = "Deleted", documentId = id });
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

static async Task MarkDocumentReadyAsync(
    KnowledgeDocumentEntity entity,
    KnowledgeDbContext db,
    KnowledgeSearchService search,
    EmbeddingService embeddings,
    AzureSearchOptions searchOptions,
    CancellationToken cancellationToken)
{
    try
    {
        if (searchOptions.Enabled)
        {
            await search.EnsureIndexAsync(cancellationToken);
            var vector = await embeddings.CreateEmbeddingAsync($"{entity.Title}\n{entity.Content}", cancellationToken);
            await search.UpsertDocumentsAsync([(entity, vector)], cancellationToken);
            var searchable = await WaitForSearchVisibilityAsync(search, entity.Id, TimeSpan.FromSeconds(15), cancellationToken);
            if (!searchable)
                throw new TimeoutException($"Azure AI Search da nhan {entity.Id} nhung chua query duoc sau 15 giay.");
            entity.IngestionMessage = "AI da doc xong va Azure AI Search da query duoc tai lieu.";
        }
        else
        {
            entity.IngestionMessage = "AI da doc xong; dang dung local keyword search fallback.";
        }

        entity.IngestionStatus = "Ready";
        entity.IngestedAt = DateTimeOffset.UtcNow;
    }
    catch (Exception ex)
    {
        entity.IngestionStatus = "Failed";
        entity.IngestionMessage = ex.Message;
    }

    entity.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(cancellationToken);
}

static async Task<bool> WaitForSearchVisibilityAsync(
    KnowledgeSearchService search,
    string documentId,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (await search.DocumentExistsAsync(documentId, cancellationToken))
            return true;

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
    }

    return await search.DocumentExistsAsync(documentId, cancellationToken);
}

static async Task EnsureDocumentColumnsAsync(KnowledgeDbContext db)
{
    var connection = db.Database.GetDbConnection();
    await connection.OpenAsync();
    try
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(Documents);";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                existing.Add(reader.GetString(1));
        }

        foreach (var (name, definition) in new[]
        {
            ("FileName", "TEXT"),
            ("ContentType", "TEXT"),
            ("IngestionStatus", "TEXT NOT NULL DEFAULT 'Ready'"),
            ("IngestionMessage", "TEXT"),
            ("IngestedAt", "TEXT")
        })
        {
            if (existing.Contains(name)) continue;
            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE Documents ADD COLUMN {name} {definition};";
            await alter.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        await connection.CloseAsync();
    }
}

static async Task SeedDemoPdfAsync(
    IWebHostEnvironment environment,
    KnowledgeDbContext db,
    PdfKnowledgeExtractor extractor,
    DocumentBlobStore blobStore,
    KnowledgeSearchService search,
    EmbeddingService embeddings,
    AzureSearchOptions searchOptions,
    ILogger logger)
{
    const string fileName = "device-replacement-policy.pdf";
    var pdfPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "test-pdfs", fileName));
    if (!File.Exists(pdfPath))
        return;

    if (await db.Documents.AnyAsync(d => d.FileName == fileName || d.Title == "Device Replacement Policy Demo Seed"))
        return;

    string content;
    await using (var stream = File.OpenRead(pdfPath))
    {
        content = extractor.ExtractText(stream);
    }
    if (string.IsNullOrWhiteSpace(content))
    {
        logger.LogWarning("Demo PDF seed {Path} khong co text de ingest.", pdfPath);
        return;
    }

    var ids = await db.Documents.Select(d => d.Id).ToListAsync();
    var entity = new KnowledgeDocumentEntity
    {
        Id = DocumentIdGenerator.Next(ids),
        Title = "Device Replacement Policy Demo Seed",
        Category = SupportCategory.IT,
        Content = content,
        FileName = fileName,
        ContentType = "application/pdf",
        IngestionStatus = "Processing",
        IngestionMessage = "Demo seed PDF dang duoc dua vao knowledge index.",
        UpdatedAt = DateTimeOffset.UtcNow
    };

    var blobUrl = await blobStore.UploadAsync(entity.Id, entity.Content);
    if (blobUrl is not null)
        entity.SourceUrl = blobUrl;

    db.Documents.Add(entity);
    await db.SaveChangesAsync();
    await MarkDocumentReadyAsync(entity, db, search, embeddings, searchOptions, CancellationToken.None);
    logger.LogInformation("Da auto-seed demo PDF {FileName} thanh {DocumentId}.", fileName, entity.Id);
}

public sealed record CreateDocumentRequest(string Title, string Category, string Content, string? SourceUrl);

// Knowledge-domain events - khong tham gia saga, chi broadcast cho subscriber khac (vd. analytics).
public sealed record KnowledgeDocumentUploaded(string DocumentId);
public sealed record KnowledgeIndexUpdated(int DocumentCount);
