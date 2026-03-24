using Azure.AI.Projects;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;

namespace DiscoveryAgent.Functions;

public class HealthFunction
{
    [Function("Health")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
        => new OkObjectResult(new { status = "healthy", timestamp = DateTime.UtcNow });

    [Function("DiagDI")]
    public IActionResult DiagDI(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "diag/di")] HttpRequest req)
    {
        var results = new Dictionary<string, string>();
        var sp = req.HttpContext.RequestServices;

        TryResolve<DiscoveryBotSettings>(sp, results);
        TryResolve<AIProjectClient>(sp, results);
        TryResolve<CosmosClient>(sp, results);
        TryResolve<Database>(sp, results);
        TryResolve<SearchClient>(sp, results);
        TryResolve<BlobServiceClient>(sp, results);
        TryResolve<IAgentManager>(sp, results);
        TryResolve<IKnowledgeStore>(sp, results);
        TryResolve<IKnowledgeQueryService>(sp, results);
        TryResolve<IContextManagementService>(sp, results);
        TryResolve<IQuestionnaireProcessor>(sp, results);
        TryResolve<IUserProfileService>(sp, results);
        TryResolve<IConversationHandler>(sp, results);

        return new OkObjectResult(results);
    }

    private static void TryResolve<T>(IServiceProvider sp, Dictionary<string, string> results)
    {
        try
        {
            var svc = sp.GetService(typeof(T));
            results[typeof(T).Name] = svc is not null ? "OK" : "null (not registered)";
        }
        catch (Exception ex)
        {
            results[typeof(T).Name] = $"FAIL: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
