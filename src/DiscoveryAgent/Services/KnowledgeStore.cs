using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Services;

/// <summary>
/// Manages knowledge extraction, storage, and retrieval with full attribution.
/// Dual-writes to Cosmos DB (persistence) and Azure AI Search (retrieval).
/// 
/// Carried forward from v1 — the knowledge model is the same.
/// Updated field names: SourceThreadId → SourceConversationId for Responses API.
/// </summary>
public class KnowledgeStore : IKnowledgeStore
{
    private readonly Database _cosmosDb;
    private readonly SearchClient? _searchClient;
    private readonly ILogger<KnowledgeStore> _logger;

    public KnowledgeStore(
        Database cosmosDb,
        ILogger<KnowledgeStore> logger,
        SearchClient? searchClient = null)
    {
        _cosmosDb = cosmosDb;
        _searchClient = searchClient;
        _logger = logger;
    }

    public async Task<string> StoreAsync(KnowledgeItem item)
    {
        // Persist to Cosmos DB (source of truth)
        var container = _cosmosDb.GetContainer("knowledge-items");
        await container.UpsertItemAsync(item, new PartitionKey(item.RelatedContextId));

        // Index in AI Search for semantic retrieval (optional, non-fatal on failure)
        if (_searchClient is null) goto done;
        try
        {
            var searchDoc = new SearchDocument(new Dictionary<string, object>
            {
                ["id"] = item.Id,
                ["content"] = item.Content,
                ["category"] = item.Category.ToString(),
                ["confidence"] = item.Confidence,
                ["sourceUserId"] = item.SourceUserId,
                ["sourceUserRole"] = item.SourceUserRole,
                ["sourceConversationId"] = item.SourceConversationId,
                ["relatedContextId"] = item.RelatedContextId,
                ["tags"] = item.Tags,
                ["extractionTimestamp"] = item.ExtractionTimestamp.ToString("O"),
                ["verified"] = item.Verified,
            });

            await _searchClient.MergeOrUploadDocumentsAsync([searchDoc]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to index knowledge item {Id} in search", item.Id);
        }

        done:
        _logger.LogInformation("Knowledge stored: {Id} [{Category}] confidence={Confidence:F2}",
            item.Id, item.Category, item.Confidence);

        return item.Id;
    }

    public async Task<List<KnowledgeItem>> GetByContextAsync(string contextId)
    {
        var container = _cosmosDb.GetContainer("knowledge-items");
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.relatedContextId = @contextId ORDER BY c.extractionTimestamp DESC")
            .WithParameter("@contextId", contextId);

        var items = new List<KnowledgeItem>();
        using var iterator = container.GetItemQueryIterator<KnowledgeItem>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            items.AddRange(response);
        }

        return items;
    }

    public async Task<List<KnowledgeItem>> SearchAsync(
        string query, string? contextId = null, int maxResults = 10)
    {
        if (_searchClient is null)
            return await GetByContextAsync(contextId ?? "default");

        var options = new SearchOptions
        {
            Size = maxResults,
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = "default"
            }
        };

        if (!string.IsNullOrEmpty(contextId))
        {
            options.Filter = $"relatedContextId eq '{contextId}'";
        }

        var results = await _searchClient.SearchAsync<KnowledgeItem>(query, options);
        var items = new List<KnowledgeItem>();

        await foreach (var result in results.Value.GetResultsAsync())
        {
            if (result.Document is not null)
                items.Add(result.Document);
        }

        return items;
    }

    public async Task<KnowledgeProvenance?> TraceOriginAsync(string itemId, string contextId)
    {
        var container = _cosmosDb.GetContainer("knowledge-items");

        try
        {
            var response = await container.ReadItemAsync<KnowledgeItem>(
                itemId, new PartitionKey(contextId));
            var item = response.Resource;

            return new KnowledgeProvenance(
                Item: item,
                SourceUserId: item.SourceUserId,
                SourceUserRole: item.SourceUserRole,
                ConversationId: item.SourceConversationId,
                ExtractionTimestamp: item.ExtractionTimestamp,
                Verified: item.Verified
            );
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
