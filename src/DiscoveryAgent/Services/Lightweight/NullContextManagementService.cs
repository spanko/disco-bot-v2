using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;

namespace DiscoveryAgent.Services.Lightweight;

/// <summary>
/// No-op context management for lightweight mode.
/// </summary>
public class NullContextManagementService : IContextManagementService
{
    public Task<DiscoveryContext?> GetContextAsync(string contextId) =>
        Task.FromResult<DiscoveryContext?>(null);

    public Task UpdateContextAsync(DiscoveryContextConfig config) =>
        Task.CompletedTask;

    public Task<List<DiscoveryContext>> ListContextsAsync() =>
        Task.FromResult(new List<DiscoveryContext>());
}
