using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DiscoveryAgent.Functions;

public class AdminFunction
{
    private readonly Database _cosmosDb;
    private readonly IContextManagementService _contextService;
    private readonly ILogger<AdminFunction> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AdminFunction(
        Database cosmosDb,
        IContextManagementService contextService,
        ILogger<AdminFunction> logger)
    {
        _cosmosDb = cosmosDb;
        _contextService = contextService;
        _logger = logger;
    }

    // ── Contexts ──────────────────────────────────────────────────────

    [Function("ListContexts")]
    public async Task<IActionResult> ListContexts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/contexts")] HttpRequest req)
    {
        var contexts = await _contextService.ListContextsAsync();
        return new OkObjectResult(contexts);
    }

    [Function("UpsertContext")]
    public async Task<IActionResult> UpsertContext(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/context")] HttpRequest req)
    {
        var context = await JsonSerializer.DeserializeAsync<DiscoveryContext>(req.Body, JsonOpts);
        if (context is null || string.IsNullOrEmpty(context.ContextId))
            return new BadRequestObjectResult(new { error = "Invalid context: contextId is required" });

        var container = _cosmosDb.GetContainer("discovery-sessions");
        // Ensure id matches contextId for Cosmos
        var doc = context with { Id = context.ContextId };
        await container.UpsertItemAsync(doc, new PartitionKey(doc.ContextId));

        _logger.LogInformation("Context upserted: {ContextId}", doc.ContextId);
        return new OkObjectResult(new { status = "upserted", contextId = doc.ContextId });
    }

    // ── Questionnaires ────────────────────────────────────────────────

    [Function("ListQuestionnaires")]
    public async Task<IActionResult> ListQuestionnaires(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/questionnaires")] HttpRequest req)
    {
        var container = _cosmosDb.GetContainer("questionnaires");
        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.uploadedAt DESC");
        var items = new List<ParsedQuestionnaire>();
        using var it = container.GetItemQueryIterator<ParsedQuestionnaire>(query);
        while (it.HasMoreResults) items.AddRange(await it.ReadNextAsync());
        return new OkObjectResult(items);
    }

    [Function("UpsertQuestionnaire")]
    public async Task<IActionResult> UpsertQuestionnaire(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/questionnaire")] HttpRequest req)
    {
        var questionnaire = await JsonSerializer.DeserializeAsync<ParsedQuestionnaire>(req.Body, JsonOpts);
        if (questionnaire is null || string.IsNullOrEmpty(questionnaire.QuestionnaireId))
            return new BadRequestObjectResult(new { error = "Invalid questionnaire: questionnaireId is required" });

        var container = _cosmosDb.GetContainer("questionnaires");
        // Ensure id matches questionnaireId for Cosmos
        var doc = questionnaire with { Id = questionnaire.QuestionnaireId };
        await container.UpsertItemAsync(doc, new PartitionKey(doc.QuestionnaireId));

        _logger.LogInformation("Questionnaire upserted: {QuestionnaireId} ({Title})",
            doc.QuestionnaireId, doc.Title);
        return new OkObjectResult(new
        {
            status = "upserted",
            questionnaireId = doc.QuestionnaireId,
            sections = doc.Sections.Count,
            questions = doc.Questions.Count
        });
    }
}
