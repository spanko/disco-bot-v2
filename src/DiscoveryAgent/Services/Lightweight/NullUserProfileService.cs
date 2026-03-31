using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;

namespace DiscoveryAgent.Services.Lightweight;

/// <summary>
/// No-op user profile service for lightweight mode.
/// </summary>
public class NullUserProfileService : IUserProfileService
{
    public Task UpsertAsync(UserProfile profile) =>
        Task.CompletedTask;

    public Task<UserProfile?> GetAsync(string userId) =>
        Task.FromResult<UserProfile?>(null);
}
