using Azure.AI.Projects;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DiscoveryAgent.Auth;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using DiscoveryAgent.Handlers;
using DiscoveryAgent.Services;
using DiscoveryAgent.Services.Lightweight;
using DiscoveryAgent.Telemetry;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ───────────────────────────────────────────────
var settings = DiscoveryBotSettings.FromEnvironment();
settings.Validate();
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

// ── Application Services (mode-dependent) ────────────────────────
builder.Services.AddSingleton<IAgentManager, AgentManager>();
builder.Services.AddSingleton<TestRunnerService>();
builder.Services.AddSingleton<CoverageAnalyzer>();

if (settings.IsLightweight)
{
    // Lightweight mode: no Cosmos, no AI Search, no Blob Storage
    builder.Services.AddSingleton<IKnowledgeStore, NullKnowledgeStore>();
    builder.Services.AddSingleton<IKnowledgeQueryService, NullKnowledgeQueryService>();
    builder.Services.AddSingleton<IContextManagementService, NullContextManagementService>();
    builder.Services.AddSingleton<IQuestionnaireProcessor, NullQuestionnaireProcessor>();
    builder.Services.AddSingleton<IUserProfileService, NullUserProfileService>();
    builder.Services.AddScoped<IConversationHandler, LightweightConversationHandler>();
}
else
{
    // Standard/Full mode: register BYO infrastructure clients
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

    if (!string.IsNullOrEmpty(settings.AiSearchEndpoint))
    {
        builder.Services.AddSingleton(_ =>
            new SearchClient(
                new Uri(settings.AiSearchEndpoint),
                settings.KnowledgeIndexName,
                credential));
    }

    // Knowledge services always use Cosmos; SearchClient is optional for semantic features
    builder.Services.AddSingleton<IKnowledgeStore>(sp =>
        new KnowledgeStore(
            sp.GetRequiredService<Database>(),
            sp.GetRequiredService<ILogger<KnowledgeStore>>(),
            sp.GetService<SearchClient>()));
    builder.Services.AddSingleton<IKnowledgeQueryService>(sp =>
        new KnowledgeQueryService(
            sp.GetRequiredService<Database>(),
            sp.GetRequiredService<ILogger<KnowledgeQueryService>>(),
            sp.GetService<SearchClient>()));

    if (!string.IsNullOrEmpty(settings.StorageEndpoint))
    {
        builder.Services.AddSingleton(_ =>
            new BlobServiceClient(new Uri(settings.StorageEndpoint), credential));
    }

    builder.Services.AddSingleton<IContextManagementService, ContextManagementService>();
    builder.Services.AddSingleton<IQuestionnaireProcessor, QuestionnaireProcessor>();
    builder.Services.AddSingleton<IUserProfileService, UserProfileService>();
    builder.Services.AddScoped<IConversationHandler, ConversationHandler>();
}

// ── Auth ─────────────────────────────────────────────────────────
IAuthService authService = settings.AuthMode switch
{
    "magic_link" => new MagicLinkAuthService(settings),
    "invite_code" => new InviteCodeAuthService(settings),
    "entra_external" => new EntraAuthService(),
    _ => new NoneAuthService(),
};
builder.Services.AddSingleton(authService);

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

// ── Auth middleware (skips /health, /api/auth/*, and static files) ──
if (settings.AuthMode != "none")
{
    app.UseMiddleware<AuthMiddleware>();
}

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
    IServiceProvider sp,
    IAgentManager agentManager) =>
{
    var checks = new Dictionary<string, string>();

    // In lightweight mode, agent init happens lazily on first request — don't gate readiness on it
    checks["agent"] = agentManager.IsInitialized ? "ok"
        : settings.IsLightweight ? "lazy_init"
        : "not_initialized";
    checks["mode"] = settings.ConversationMode;

    var cosmosClient = sp.GetService<CosmosClient>();
    if (cosmosClient is not null)
    {
        try { await cosmosClient.ReadAccountAsync(); checks["cosmos"] = "ok"; }
        catch { checks["cosmos"] = "failed"; }
    }

    var searchClient = sp.GetService<SearchClient>();
    if (searchClient is not null)
    {
        try { await searchClient.GetDocumentCountAsync(); checks["aiSearch"] = "ok"; }
        catch { checks["aiSearch"] = "failed"; }
    }

    var blobClient = sp.GetService<BlobServiceClient>();
    if (blobClient is not null)
    {
        try { await blobClient.GetPropertiesAsync(); checks["storage"] = "ok"; }
        catch { checks["storage"] = "failed"; }
    }

    // Only gate readiness on agent init — infra checks (cosmos/search/storage) can be degraded
    var agentReady = checks["agent"] is "ok" or "lazy_init";
    var allHealthy = checks.Values.All(v => v is "ok" or "lazy_init" or "lightweight" or "standard" or "full");
    var status = allHealthy ? "healthy" : "degraded";
    return agentReady
        ? Results.Ok(new { status, checks, timestamp = DateTime.UtcNow })
        : Results.Json(new { status, checks, timestamp = DateTime.UtcNow }, statusCode: 503);
});

// =====================================================================
// Auth API endpoints
// =====================================================================

app.MapGet("/api/auth/mode", () =>
    Results.Ok(new { authMode = settings.AuthMode }));

if (settings.AuthMode == "magic_link")
{
    var magicLinkService = (MagicLinkAuthService)authService;

    app.MapPost("/api/auth/magic-link", (HttpRequest req) =>
    {
        var body = req.ReadFromJsonAsync<MagicLinkRequest>().Result;
        if (body is null || string.IsNullOrEmpty(body.Email))
            return Results.BadRequest(new { error = "Email is required" });

        var token = magicLinkService.GenerateToken(body.Email, body.ContextId);
        var host = $"{req.Scheme}://{req.Host}";
        var verifyUrl = $"{host}/api/auth/verify?token={Uri.EscapeDataString(token)}";

        return Results.Ok(new
        {
            token,
            verifyUrl,
            expiresInHours = settings.MagicLinkExpiryHours,
        });
    });

    app.MapGet("/api/auth/verify", async (string token, HttpResponse response) =>
    {
        var result = await magicLinkService.ValidateTokenAsync(token);
        if (!result.IsAuthenticated)
            return Results.Json(new { error = "Invalid or expired token" }, statusCode: 401);

        response.Cookies.Append(MagicLinkAuthService.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromHours(settings.MagicLinkExpiryHours),
        });

        // Redirect to home page after successful verification
        return Results.Redirect("/");
    });
}

if (settings.AuthMode == "invite_code")
{
    var inviteCodeService = (InviteCodeAuthService)authService;

    app.MapPost("/api/auth/validate-code", (HttpRequest req, HttpResponse response) =>
    {
        var body = req.ReadFromJsonAsync<InviteCodeRequest>().Result;
        if (body is null || string.IsNullOrEmpty(body.Code))
            return Results.BadRequest(new { error = "Code is required" });

        var contextId = inviteCodeService.ValidateCode(body.Code);
        if (contextId is null)
            return Results.Json(new { error = "Invalid invite code" }, statusCode: 401);

        var userId = body.DisplayName ?? $"invite-{Guid.NewGuid():N}"[..16];
        var sessionToken = inviteCodeService.GenerateSessionToken(contextId, userId);

        if (sessionToken is not null)
        {
            response.Cookies.Append(InviteCodeAuthService.CookieName, sessionToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromHours(12),
            });
        }

        return Results.Ok(new
        {
            contextId,
            userId,
            authenticated = true,
        });
    });
}

app.MapPost("/api/auth/logout", (HttpResponse response) =>
{
    response.Cookies.Delete("disco-auth");
    return Results.Ok(new { status = "logged_out" });
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
        var isDev = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
        return Results.Json(new { error = isDev ? $"{ex.GetType().Name}: {ex.Message}" : "Internal error" }, statusCode: 500);
    }
});

// =====================================================================
// Conversation History API
// =====================================================================

app.MapGet("/api/conversations/{conversationId}/history", async (
    string conversationId, Database cosmosDb) =>
{
    var container = cosmosDb.GetContainer("conversation-turns");
    var query = new QueryDefinition(
        "SELECT * FROM c WHERE c.conversationId = @convId ORDER BY c.timestamp ASC")
        .WithParameter("@convId", conversationId);

    var turns = new List<ConversationTurn>();
    using var it = container.GetItemQueryIterator<ConversationTurn>(query);
    while (it.HasMoreResults) turns.AddRange(await it.ReadNextAsync());
    return Results.Ok(turns);
});

app.MapGet("/api/conversations", async (
    string? contextId, Database cosmosDb) =>
{
    var container = cosmosDb.GetContainer("conversation-turns");
    var sql = string.IsNullOrEmpty(contextId)
        ? "SELECT DISTINCT c.conversationId, c.contextId, c.userId, MIN(c.timestamp) as started, MAX(c.timestamp) as lastActivity, COUNT(1) as turnCount FROM c GROUP BY c.conversationId, c.contextId, c.userId"
        : "SELECT DISTINCT c.conversationId, c.contextId, c.userId, MIN(c.timestamp) as started, MAX(c.timestamp) as lastActivity, COUNT(1) as turnCount FROM c WHERE c.contextId = @ctx GROUP BY c.conversationId, c.contextId, c.userId";

    var query = new QueryDefinition(sql);
    if (!string.IsNullOrEmpty(contextId))
        query = query.WithParameter("@ctx", contextId);

    var convos = new List<dynamic>();
    using var it = container.GetItemQueryIterator<dynamic>(query);
    while (it.HasMoreResults) convos.AddRange(await it.ReadNextAsync());
    return Results.Ok(convos);
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

if (!settings.IsLightweight)
{
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
}

// =====================================================================
// Document Upload APIs (requires Blob Storage — standard/full mode only)
// =====================================================================

if (!settings.IsLightweight)
{

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

} // end if (!settings.IsLightweight) — document upload routes

// =====================================================================
// Test Harness API
// =====================================================================

app.MapPost("/api/test/run", async (
    HttpRequest req,
    HttpResponse res,
    IServiceProvider sp,
    TestRunnerService testRunner,
    IKnowledgeStore knowledgeStore,
    ILogger<TestRunnerService> log,
    CancellationToken ct) =>
{
    var request = await JsonSerializer.DeserializeAsync<TestRunRequest>(req.Body, jsonOpts, ct);
    if (request is null)
        return Results.BadRequest(new { error = "Invalid request body" });

    // SSE streaming response
    res.Headers.ContentType = "text/event-stream";
    res.Headers.CacheControl = "no-cache";
    res.Headers.Connection = "keep-alive";

    // Create a scoped handler for this test run
    using var scope = sp.CreateScope();
    var handler = scope.ServiceProvider.GetRequiredService<IConversationHandler>();

    // Start a heartbeat timer to keep the SSE connection alive during long LLM calls
    using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    var heartbeatTask = Task.Run(async () =>
    {
        while (!heartbeatCts.Token.IsCancellationRequested)
        {
            await Task.Delay(15000, heartbeatCts.Token).ConfigureAwait(false);
            try { await res.WriteAsync(": heartbeat\n\n", heartbeatCts.Token); await res.Body.FlushAsync(heartbeatCts.Token); }
            catch { break; }
        }
    }, heartbeatCts.Token);

    try
    {
        await foreach (var evt in testRunner.RunAsync(request, handler, knowledgeStore, ct))
        {
            var json = JsonSerializer.Serialize(evt, jsonOpts);
            var eventType = evt is TestCompleteEvent ? "complete" : "turn";
            await res.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
            await res.Body.FlushAsync(ct);
        }
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Test run failed");
        var errorJson = JsonSerializer.Serialize(new { type = "error", error = $"{ex.GetType().Name}: {ex.Message}" }, jsonOpts);
        await res.WriteAsync($"event: error\ndata: {errorJson}\n\n", ct);
        await res.Body.FlushAsync(ct);
    }

    heartbeatCts.Cancel();
    try { await heartbeatTask; } catch { /* expected */ }

    return Results.Empty;
});

app.MapGet("/api/test/coverage/{contextId}", async (
    string contextId,
    CoverageAnalyzer coverageAnalyzer) =>
{
    var result = await coverageAnalyzer.AnalyzeAsync(contextId);
    return Results.Ok(result);
});

app.MapPost("/api/test/batch", async (
    HttpRequest req,
    HttpResponse res,
    IServiceProvider sp,
    TestRunnerService testRunner,
    IKnowledgeStore knowledgeStore,
    ILogger<TestRunnerService> log,
    CancellationToken ct) =>
{
    var body = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body, jsonOpts, ct);

    var contextId = body.GetProperty("contextId").GetString() ?? "";
    var maxTurns = body.TryGetProperty("maxTurns", out var mt) ? mt.GetInt32() : 60;
    var runsArray = body.GetProperty("runs");

    // SSE streaming response for batch
    res.Headers.ContentType = "text/event-stream";
    res.Headers.CacheControl = "no-cache";
    res.Headers.Connection = "keep-alive";

    int runIndex = 0;
    foreach (var runDef in runsArray.EnumerateArray())
    {
        var personaId = runDef.GetProperty("persona").GetString() ?? "";
        var responseMode = runDef.GetProperty("responseMode").GetString() ?? "realistic";

        var persona = ResolvePersona(personaId);
        var runRequest = new TestRunRequest
        {
            ContextId = contextId,
            Persona = persona,
            ResponseMode = responseMode,
            MaxTurns = maxTurns,
        };

        // Emit run start event
        var startJson = JsonSerializer.Serialize(new { type = "run_start", runIndex, persona = persona.Name, responseMode }, jsonOpts);
        await res.WriteAsync($"event: run_start\ndata: {startJson}\n\n", ct);
        await res.Body.FlushAsync(ct);

        using var scope = sp.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IConversationHandler>();

        await foreach (var evt in testRunner.RunAsync(runRequest, handler, knowledgeStore, ct))
        {
            var json = JsonSerializer.Serialize(evt, jsonOpts);
            var eventType = evt is TestCompleteEvent ? "run_complete" : "turn";
            await res.WriteAsync($"event: {eventType}\ndata: {json}\n\n", ct);
            await res.Body.FlushAsync(ct);
        }

        runIndex++;
    }

    var doneJson = JsonSerializer.Serialize(new { type = "batch_complete", totalRuns = runIndex }, jsonOpts);
    await res.WriteAsync($"event: batch_complete\ndata: {doneJson}\n\n", ct);
    await res.Body.FlushAsync(ct);

    return Results.Empty;
});

static TestPersona ResolvePersona(string personaId)
{
    return personaId switch
    {
        "exec" => new TestPersona { Id = "exec", Name = "Executive sponsor", Style = "brief, strategic, time-pressured", Depth = "high-level", Traits = ["avoids details", "speaks in outcomes", "name-drops stakeholders", "redirects to ROI"] },
        "mgr" => new TestPersona { Id = "mgr", Name = "Mid-level manager", Style = "cooperative, structured, wants to impress", Depth = "moderate detail", Traits = ["gives balanced answers", "caveats often", "references team dynamics", "mentions process"] },
        "ic" => new TestPersona { Id = "ic", Name = "Individual contributor", Style = "candid, sometimes frustrated, detail-oriented", Depth = "deep technical", Traits = ["shares pain points freely", "gives specific examples", "may go on tangents", "mentions tooling gaps"] },
        "new" => new TestPersona { Id = "new", Name = "New hire (< 3 months)", Style = "uncertain, deferential, limited context", Depth = "surface only", Traits = ["says 'I think' or 'I'm not sure' often", "references onboarding gaps", "compares to previous employer", "asks clarifying questions back"] },
        "skeptic" => new TestPersona { Id = "skeptic", Name = "Skeptical veteran", Style = "guarded, tests the agent, challenges premises", Depth = "selective", Traits = ["pushes back on questions", "gives one-word answers initially", "opens up if trusted", "flags past failed initiatives"] },
        "eager" => new TestPersona { Id = "eager", Name = "Enthusiastic champion", Style = "verbose, optimistic, wants to help", Depth = "exhaustive", Traits = ["over-shares", "rates everything positively", "hard to get honest negatives from", "volunteers for follow-ups"] },
        _ => new TestPersona { Id = personaId, Name = personaId, Style = "cooperative", Depth = "moderate", Traits = ["general respondent"] },
    };
}

// ── Fallback: serve index.html for SPA-style routing ────────────
app.MapFallbackToFile("index.html");

app.Run();

// ── Auth request models ──────────────────────────────────────────
record MagicLinkRequest(string Email, string? ContextId);
record InviteCodeRequest(string Code, string? DisplayName);
