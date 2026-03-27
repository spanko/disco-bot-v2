using Azure.AI.Projects;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using DiscoveryAgent.Handlers;
using DiscoveryAgent.Services;
using DiscoveryAgent.Telemetry;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ───────────────────────────────────────────────
var config = builder.Configuration;
var settings = config.GetSection("DiscoveryBot").Get<DiscoveryBotSettings>()
    ?? DiscoveryBotSettings.FromEnvironment();
builder.Services.AddSingleton(settings);

// ── OpenTelemetry + Azure Monitor ────────────────────────────────
if (!string.IsNullOrEmpty(settings.AppInsightsConnectionString))
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor(options =>
        {
            options.ConnectionString = settings.AppInsightsConnectionString;
        })
        .WithMetrics(metrics =>
        {
            metrics.AddMeter(DiscoveryMetrics.Meter.Name);
        });
}

// ── Azure Credential ────────────────────────────────────────────
var credential = new DefaultAzureCredential();

// ── Foundry Project Client (GA SDK) ─────────────────────────────
builder.Services.AddSingleton(_ =>
    new AIProjectClient(new Uri(settings.ProjectEndpoint), credential));

// ── BYO Cosmos DB ───────────────────────────────────────────────
builder.Services.AddSingleton(_ =>
    new CosmosClient(settings.CosmosEndpoint, credential, new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        },
    }));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<CosmosClient>().GetDatabase(settings.CosmosDatabase));

// ── BYO AI Search ───────────────────────────────────────────────
builder.Services.AddSingleton(_ =>
    new SearchClient(
        new Uri(settings.AiSearchEndpoint),
        settings.KnowledgeIndexName,
        credential));

// ── BYO Blob Storage ────────────────────────────────────────────
builder.Services.AddSingleton(_ =>
    new BlobServiceClient(new Uri(settings.StorageEndpoint), credential));

// ── Application Services ────────────────────────────────────────
builder.Services.AddSingleton<IAgentManager, AgentManager>();
builder.Services.AddSingleton<IKnowledgeStore, KnowledgeStore>();
builder.Services.AddSingleton<IKnowledgeQueryService, KnowledgeQueryService>();
builder.Services.AddSingleton<IContextManagementService, ContextManagementService>();
builder.Services.AddSingleton<IQuestionnaireProcessor, QuestionnaireProcessor>();
builder.Services.AddSingleton<IUserProfileService, UserProfileService>();

builder.Services.AddScoped<IConversationHandler, ConversationHandler>();

var app = builder.Build();

// =====================================================================
// Initialize agent BEFORE accepting requests.
// =====================================================================
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

try
{
    logger.LogInformation("Ensuring agent definition exists in Foundry...");
    var agentManager = app.Services.GetRequiredService<IAgentManager>();
    await agentManager.EnsureAgentExistsAsync();
    logger.LogInformation("Agent ready: {AgentName}", agentManager.AgentName);
}
catch (Exception ex)
{
    logger.LogCritical(ex,
        "Agent initialization FAILED. Check: (1) PROJECT_ENDPOINT is correct, " +
        "(2) model deployment '{Model}' exists, (3) managed identity has Azure AI User role.",
        settings.ModelDeploymentName);
}

// ── Static files (web UI from wwwroot/) ─────────────────────────
app.UseStaticFiles();

// ── JSON options ────────────────────────────────────────────────
var jsonOpts = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

// =====================================================================
// Health endpoints
// =====================================================================

app.MapGet("/health", () =>
    Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.MapGet("/health/ready", async (
    CosmosClient cosmosClient,
    SearchClient searchClient,
    BlobServiceClient blobClient) =>
{
    var checks = new Dictionary<string, string>();

    try { await cosmosClient.ReadAccountAsync(); checks["cosmos"] = "ok"; }
    catch { checks["cosmos"] = "failed"; }

    try { await searchClient.GetDocumentCountAsync(); checks["aiSearch"] = "ok"; }
    catch { checks["aiSearch"] = "failed"; }

    try { await blobClient.GetPropertiesAsync(); checks["storage"] = "ok"; }
    catch { checks["storage"] = "failed"; }

    var allHealthy = checks.Values.All(v => v == "ok");
    return allHealthy
        ? Results.Ok(new { status = "healthy", checks, timestamp = DateTime.UtcNow })
        : Results.Json(new { status = "degraded", checks, timestamp = DateTime.UtcNow }, statusCode: 503);
});

// =====================================================================
// Conversation API
// =====================================================================

app.MapPost("/api/conversation", async (
    HttpRequest req,
    IConversationHandler handler,
    ILogger<ConversationHandler> log,
    CancellationToken ct) =>
{
    try
    {
        var request = await JsonSerializer.DeserializeAsync<ConversationRequest>(
            req.Body, jsonOpts, ct);

        if (request is null || string.IsNullOrEmpty(request.Message))
            return Results.BadRequest(new { error = "Message is required" });

        var response = await handler.HandleAsync(request, ct);
        return Results.Ok(response);
    }
    catch (InvalidOperationException ex)
    {
        log.LogError(ex, "Agent not ready");
        return Results.Json(new { error = ex.Message }, statusCode: 503);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Conversation failed");
        return Results.Json(new { error = "Internal error" }, statusCode: 500);
    }
});

// =====================================================================
// Knowledge APIs
// =====================================================================

app.MapGet("/api/knowledge/{contextId}", async (
    string contextId, int? skip, int? take,
    IKnowledgeQueryService queryService) =>
{
    var items = await queryService.GetByContextPaginatedAsync(contextId, skip ?? 0, take ?? 50);
    return Results.Ok(items);
});

app.MapGet("/api/knowledge/{contextId}/search", async (
    string contextId, string? q,
    IKnowledgeStore store) =>
{
    if (string.IsNullOrEmpty(q))
        return Results.BadRequest(new { error = "q parameter required" });
    var items = await store.SearchAsync(q, contextId);
    return Results.Ok(items);
});

app.MapGet("/api/knowledge/{contextId}/summary", async (
    string contextId,
    IKnowledgeQueryService queryService) =>
{
    var summary = await queryService.GetCategorySummaryAsync(contextId);
    return Results.Ok(summary);
});

app.MapGet("/api/knowledge/{contextId}/provenance/{itemId}", async (
    string contextId, string itemId,
    IKnowledgeStore store) =>
{
    var provenance = await store.TraceOriginAsync(itemId, contextId);
    return provenance is null ? Results.NotFound() : Results.Ok(provenance);
});

// =====================================================================
// Admin APIs
// =====================================================================

app.MapGet("/api/manage/contexts", async (IContextManagementService contextService) =>
{
    var contexts = await contextService.ListContextsAsync();
    return Results.Ok(contexts);
});

app.MapPost("/api/manage/context", async (
    HttpRequest req,
    Database cosmosDb,
    ILogger<Program> log) =>
{
    var context = await JsonSerializer.DeserializeAsync<DiscoveryContext>(req.Body, jsonOpts);
    if (context is null || string.IsNullOrEmpty(context.ContextId))
        return Results.BadRequest(new { error = "Invalid context: contextId is required" });

    var container = cosmosDb.GetContainer("discovery-sessions");
    var doc = context with { Id = context.ContextId };
    await container.UpsertItemAsync(doc, new PartitionKey(doc.ContextId));

    log.LogInformation("Context upserted: {ContextId}", doc.ContextId);
    return Results.Ok(new { status = "upserted", contextId = doc.ContextId });
});

app.MapGet("/api/manage/questionnaires", async (Database cosmosDb) =>
{
    var container = cosmosDb.GetContainer("questionnaires");
    var query = new QueryDefinition("SELECT * FROM c ORDER BY c.uploadedAt DESC");
    var items = new List<ParsedQuestionnaire>();
    using var it = container.GetItemQueryIterator<ParsedQuestionnaire>(query);
    while (it.HasMoreResults) items.AddRange(await it.ReadNextAsync());
    return Results.Ok(items);
});

app.MapPost("/api/manage/questionnaire", async (
    HttpRequest req,
    Database cosmosDb,
    ILogger<Program> log) =>
{
    var questionnaire = await JsonSerializer.DeserializeAsync<ParsedQuestionnaire>(req.Body, jsonOpts);
    if (questionnaire is null || string.IsNullOrEmpty(questionnaire.QuestionnaireId))
        return Results.BadRequest(new { error = "Invalid questionnaire: questionnaireId is required" });

    var container = cosmosDb.GetContainer("questionnaires");
    var doc = questionnaire with { Id = questionnaire.QuestionnaireId };
    await container.UpsertItemAsync(doc, new PartitionKey(doc.QuestionnaireId));

    log.LogInformation("Questionnaire upserted: {QuestionnaireId} ({Title})",
        doc.QuestionnaireId, doc.Title);
    return Results.Ok(new
    {
        status = "upserted",
        questionnaireId = doc.QuestionnaireId,
        sections = doc.Sections.Count,
        questions = doc.Questions.Count
    });
});

// =====================================================================
// Document Upload APIs
// =====================================================================

var allowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "application/pdf",
    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    "application/msword",
    "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/tiff",
};

app.MapPost("/api/documents/upload", async (
    HttpRequest req,
    BlobServiceClient blobService,
    ILogger<Program> log) =>
{
    if (!req.HasFormContentType || req.Form.Files.Count == 0)
        return Results.BadRequest(new { error = "No file uploaded. Use multipart/form-data." });

    var file = req.Form.Files[0];
    if (file.Length == 0)
        return Results.BadRequest(new { error = "File is empty." });

    if (file.Length > 20 * 1024 * 1024)
        return Results.BadRequest(new { error = "File too large. Max 20 MB." });

    var contentType = file.ContentType?.ToLowerInvariant() ?? "";
    if (!allowedContentTypes.Contains(contentType))
        return Results.BadRequest(new
        {
            error = $"Unsupported file type: {contentType}",
            allowed = allowedContentTypes
        });

    var userId = req.Form["userId"].ToString();
    if (string.IsNullOrEmpty(userId)) userId = "anonymous";

    var contextId = req.Form["contextId"].ToString();
    if (string.IsNullOrEmpty(contextId)) contextId = "default";

    var documentId = Guid.NewGuid().ToString();
    var ext = Path.GetExtension(file.FileName) ?? "";
    var blobName = $"{contextId}/{userId}/{documentId}{ext}";

    try
    {
        var container = blobService.GetBlobContainerClient("documents");
        await container.CreateIfNotExistsAsync();

        var blob = container.GetBlobClient(blobName);
        var headers = new BlobHttpHeaders { ContentType = contentType };

        using var stream = file.OpenReadStream();
        await blob.UploadAsync(stream, new BlobUploadOptions { HttpHeaders = headers });

        await blob.SetMetadataAsync(new Dictionary<string, string>
        {
            ["originalName"] = file.FileName,
            ["userId"] = userId,
            ["contextId"] = contextId,
            ["uploadedAt"] = DateTime.UtcNow.ToString("O"),
        });

        var isImage = contentType.StartsWith("image/");

        log.LogInformation(
            "Document uploaded: {DocumentId} ({FileName}, {Size} bytes, {Type}) by {UserId}",
            documentId, file.FileName, file.Length, contentType, userId);

        return Results.Ok(new
        {
            documentId,
            fileName = file.FileName,
            contentType,
            size = file.Length,
            blobUrl = blob.Uri.ToString(),
            isImage,
        });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Document upload failed: {FileName}", file.FileName);
        return Results.Json(new { error = "Upload failed" }, statusCode: 500);
    }
});

app.MapGet("/api/documents/{documentId}/content", async (
    string documentId,
    BlobServiceClient blobService,
    ILogger<Program> log) =>
{
    try
    {
        var container = blobService.GetBlobContainerClient("documents");

        await foreach (var blob in container.GetBlobsAsync())
        {
            if (blob.Name.Contains(documentId))
            {
                var blobClient = container.GetBlobClient(blob.Name);
                var props = await blobClient.GetPropertiesAsync();
                var ct = props.Value.ContentType;
                var isImage = ct.StartsWith("image/");

                string GetMeta(string key) =>
                    props.Value.Metadata.TryGetValue(key, out var value) ? value : "";

                if (isImage)
                {
                    using var ms = new MemoryStream();
                    await blobClient.DownloadToAsync(ms);
                    var base64 = Convert.ToBase64String(ms.ToArray());
                    return Results.Ok(new
                    {
                        documentId,
                        contentType = ct,
                        isImage = true,
                        base64Data = base64,
                        fileName = GetMeta("originalName"),
                    });
                }
                else
                {
                    return Results.Ok(new
                    {
                        documentId,
                        contentType = ct,
                        isImage = false,
                        blobUrl = blobClient.Uri.ToString(),
                        fileName = GetMeta("originalName"),
                    });
                }
            }
        }

        return Results.NotFound(new { error = "Document not found" });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Failed to get document: {DocumentId}", documentId);
        return Results.Json(new { error = "Failed to retrieve document" }, statusCode: 500);
    }
});

// ── Fallback: serve index.html for SPA-style routing ────────────
app.MapFallbackToFile("index.html");

app.Run();
