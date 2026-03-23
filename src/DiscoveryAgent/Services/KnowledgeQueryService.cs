using Azure.Search.Documents;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Services;

/// <summary>
/// Rich query layer for the admin/reporting experience (WP-3).
/// First-class citizen — not an afterthought.
/// </summary>
public class KnowledgeQueryService : IKnowledgeQueryService
{
    private readonly Database _cosmosDb;
    private readonly SearchClient _searchClient;
    private readonly ILogger<KnowledgeQueryService> _logger;

    public KnowledgeQueryService(Database cosmosDb, SearchClient searchClient, ILogger<KnowledgeQueryService> logger)
    {
        _cosmosDb = cosmosDb;
        _searchClient = searchClient;
        _logger = logger;
    }

    public async Task<List<KnowledgeItem>> GetByContextPaginatedAsync(string contextId, int skip, int take)
    {
        var container = _cosmosDb.GetContainer("knowledge-items");
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.relatedContextId = @ctx ORDER BY c.extractionTimestamp DESC OFFSET @skip LIMIT @take")
            .WithParameter("@ctx", contextId)
            .WithParameter("@skip", skip)
            .WithParameter("@take", take);

        var items = new List<KnowledgeItem>();
        using var it = container.GetItemQueryIterator<KnowledgeItem>(query);
        while (it.HasMoreResults) items.AddRange(await it.ReadNextAsync());
        return items;
    }

    public Task<List<KnowledgeItem>> SearchSemanticAsync(string query, string? contextId, int maxResults)
    {
        // Delegates to AI Search semantic query — same as KnowledgeStore.SearchAsync
        // but exposed via the query service interface for the admin layer.
        throw new NotImplementedException("WP-3: Wire up semantic search");
    }

    public async Task<Dictionary<KnowledgeCategory, int>> GetCategorySummaryAsync(string contextId)
    {
        var container = _cosmosDb.GetContainer("knowledge-items");
        var query = new QueryDefinition(
            "SELECT c.category, COUNT(1) as count FROM c WHERE c.relatedContextId = @ctx GROUP BY c.category")
            .WithParameter("@ctx", contextId);

        var result = new Dictionary<KnowledgeCategory, int>();
        using var it = container.GetItemQueryIterator<dynamic>(query);
        while (it.HasMoreResults)
        {
            foreach (var item in await it.ReadNextAsync())
            {
                if (Enum.TryParse<KnowledgeCategory>((string)item.category, true, out var cat))
                    result[cat] = (int)item.count;
            }
        }
        return result;
    }

    public Task<List<KnowledgeItem>> GetContradictionsAsync(string contextId)
        => throw new NotImplementedException("WP-3: Contradiction detection query");

    public Task<List<KnowledgeItem>> GetByUserAsync(string contextId, string userId)
        => throw new NotImplementedException("WP-3: User-filtered query");

    public Task<byte[]> ExportAsync(string contextId, string format)
        => throw new NotImplementedException("WP-3: Export to JSON/CSV");
}
