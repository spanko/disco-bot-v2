using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Services;

public class ContextManagementService : IContextManagementService
{
    private readonly Database _cosmosDb;
    private readonly ILogger<ContextManagementService> _logger;

    public ContextManagementService(Database cosmosDb, ILogger<ContextManagementService> logger)
    { _cosmosDb = cosmosDb; _logger = logger; }

    public async Task<DiscoveryContext?> GetContextAsync(string contextId)
    {
        try
        {
            var container = _cosmosDb.GetContainer("discovery-sessions");
            var response = await container.ReadItemAsync<DiscoveryContext>(contextId, new PartitionKey(contextId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { return null; }
    }

    public async Task UpdateContextAsync(DiscoveryContextConfig config)
    {
        var container = _cosmosDb.GetContainer("discovery-sessions");
        var context = new DiscoveryContext
        {
            Id = config.ContextId, ContextId = config.ContextId,
            Name = config.Name, Description = config.Description,
            DiscoveryMode = Enum.Parse<DiscoveryMode>(config.DiscoveryMode, true),
            DiscoveryAreas = config.DiscoveryAreas, KeyQuestions = config.KeyQuestions,
            SensitiveAreas = config.SensitiveAreas, SuccessCriteria = config.SuccessCriteria,
            AgentInstructions = config.AgentInstructions,
        };
        await container.UpsertItemAsync(context, new PartitionKey(context.ContextId));
        _logger.LogInformation("Context updated: {ContextId}", config.ContextId);
    }

    public async Task<List<DiscoveryContext>> ListContextsAsync()
    {
        var container = _cosmosDb.GetContainer("discovery-sessions");
        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.createdAt DESC");
        var items = new List<DiscoveryContext>();
        using var it = container.GetItemQueryIterator<DiscoveryContext>(query);
        while (it.HasMoreResults) items.AddRange(await it.ReadNextAsync());
        return items;
    }
}
