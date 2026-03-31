using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;

namespace DiscoveryAgent.Services.Lightweight;

/// <summary>
/// No-op knowledge query service for lightweight mode.
/// </summary>
public class NullKnowledgeQueryService : IKnowledgeQueryService
{
    public Task<List<KnowledgeItem>> GetByContextPaginatedAsync(string contextId, int skip, int take) =>
        Task.FromResult(new List<KnowledgeItem>());

    public Task<List<KnowledgeItem>> SearchSemanticAsync(string query, string? contextId, int maxResults) =>
        Task.FromResult(new List<KnowledgeItem>());

    public Task<Dictionary<KnowledgeCategory, int>> GetCategorySummaryAsync(string contextId) =>
        Task.FromResult(new Dictionary<KnowledgeCategory, int>());

    public Task<List<KnowledgeItem>> GetContradictionsAsync(string contextId) =>
        Task.FromResult(new List<KnowledgeItem>());

    public Task<List<KnowledgeItem>> GetByUserAsync(string contextId, string userId) =>
        Task.FromResult(new List<KnowledgeItem>());

    public Task<byte[]> ExportAsync(string contextId, string format) =>
        Task.FromResult(Array.Empty<byte>());
}
