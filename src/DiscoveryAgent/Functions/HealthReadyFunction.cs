using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;

namespace DiscoveryAgent.Functions;

public class HealthReadyFunction
{
    private readonly CosmosClient _cosmosClient;
    private readonly SearchClient _searchClient;
    private readonly BlobServiceClient _blobClient;

    public HealthReadyFunction(CosmosClient cosmosClient, SearchClient searchClient, BlobServiceClient blobClient)
    {
        _cosmosClient = cosmosClient;
        _searchClient = searchClient;
        _blobClient = blobClient;
    }

    [Function("HealthReady")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/ready")] HttpRequest req)
    {
        var checks = new Dictionary<string, string>();

        try { await _cosmosClient.ReadAccountAsync(); checks["cosmos"] = "ok"; }
        catch { checks["cosmos"] = "failed"; }

        try { await _searchClient.GetDocumentCountAsync(); checks["aiSearch"] = "ok"; }
        catch { checks["aiSearch"] = "failed"; }

        try { await _blobClient.GetPropertiesAsync(); checks["storage"] = "ok"; }
        catch { checks["storage"] = "failed"; }

        var allHealthy = checks.Values.All(v => v == "ok");
        return allHealthy
            ? new OkObjectResult(new { status = "healthy", checks, timestamp = DateTime.UtcNow })
            : new ObjectResult(new { status = "degraded", checks, timestamp = DateTime.UtcNow }) { StatusCode = 503 };
    }
}
