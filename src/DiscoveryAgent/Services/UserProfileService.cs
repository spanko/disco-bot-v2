using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Azure.Cosmos;

namespace DiscoveryAgent.Services;

public class UserProfileService : IUserProfileService
{
    private readonly Database _cosmosDb;
    public UserProfileService(Database cosmosDb) { _cosmosDb = cosmosDb; }

    public async Task UpsertAsync(UserProfile profile)
    {
        var container = _cosmosDb.GetContainer("user-profiles");
        await container.UpsertItemAsync(profile, new PartitionKey(profile.UserId));
    }

    public async Task<UserProfile?> GetAsync(string userId)
    {
        try
        {
            var container = _cosmosDb.GetContainer("user-profiles");
            var response = await container.ReadItemAsync<UserProfile>(userId, new PartitionKey(userId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        { return null; }
    }
}
