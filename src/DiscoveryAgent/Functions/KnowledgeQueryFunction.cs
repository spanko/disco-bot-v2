using DiscoveryAgent.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Functions;

public class KnowledgeQueryFunction
{
    private readonly IKnowledgeStore _store;
    private readonly IKnowledgeQueryService _queryService;
    private readonly ILogger<KnowledgeQueryFunction> _logger;

    public KnowledgeQueryFunction(IKnowledgeStore store, IKnowledgeQueryService queryService, ILogger<KnowledgeQueryFunction> logger)
    { _store = store; _queryService = queryService; _logger = logger; }

    [Function("GetKnowledge")]
    public async Task<IActionResult> GetByContext(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "knowledge/{contextId}")] HttpRequest req,
        string contextId)
    {
        var skip = int.TryParse(req.Query["skip"], out var s) ? s : 0;
        var take = int.TryParse(req.Query["take"], out var t) ? t : 50;
        var items = await _queryService.GetByContextPaginatedAsync(contextId, skip, take);
        return new OkObjectResult(items);
    }

    [Function("SearchKnowledge")]
    public async Task<IActionResult> Search(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "knowledge/{contextId}/search")] HttpRequest req,
        string contextId)
    {
        var query = req.Query["q"].ToString();
        if (string.IsNullOrEmpty(query)) return new BadRequestObjectResult(new { error = "q parameter required" });
        var items = await _store.SearchAsync(query, contextId);
        return new OkObjectResult(items);
    }

    [Function("KnowledgeSummary")]
    public async Task<IActionResult> Summary(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "knowledge/{contextId}/summary")] HttpRequest req,
        string contextId)
    {
        var summary = await _queryService.GetCategorySummaryAsync(contextId);
        return new OkObjectResult(summary);
    }

    [Function("KnowledgeProvenance")]
    public async Task<IActionResult> Provenance(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "knowledge/{contextId}/provenance/{itemId}")] HttpRequest req,
        string contextId, string itemId)
    {
        var provenance = await _store.TraceOriginAsync(itemId, contextId);
        return provenance is null ? new NotFoundResult() : new OkObjectResult(provenance);
    }
}
