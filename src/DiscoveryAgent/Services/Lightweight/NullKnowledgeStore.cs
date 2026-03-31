using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;

namespace DiscoveryAgent.Services.Lightweight;

/// <summary>
/// No-op knowledge store for lightweight mode. Tool calls succeed but data is not persisted.
/// </summary>
public class NullKnowledgeStore : IKnowledgeStore
{
    public Task<string> StoreAsync(KnowledgeItem item) =>
        Task.FromResult(Guid.NewGuid().ToString());

    public Task<List<KnowledgeItem>> GetByContextAsync(string contextId) =>
        Task.FromResult(new List<KnowledgeItem>());

    public Task<List<KnowledgeItem>> SearchAsync(string query, string? contextId = null, int maxResults = 10) =>
        Task.FromResult(new List<KnowledgeItem>());

    public Task<KnowledgeProvenance?> TraceOriginAsync(string itemId, string contextId) =>
        Task.FromResult<KnowledgeProvenance?>(null);
}
