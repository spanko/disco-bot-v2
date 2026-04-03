using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using ManagementPlane.Models;
using Microsoft.Azure.Cosmos;

namespace ManagementPlane.Services;

/// <summary>
/// Orchestrates stamp provisioning and lifecycle management via ARM deployments.
/// Stamps are tracked in a Cosmos DB container in the management database.
/// </summary>
public class StampManager
{
    private readonly ArmClient _armClient;
    private readonly Container _stampsContainer;
    private readonly ILogger<StampManager> _logger;
    private readonly string _templateSpecId;
    private readonly string _deployerObjectId;

    public StampManager(ArmClient armClient, Container stampsContainer, ILogger<StampManager> logger)
    {
        _armClient = armClient;
        _stampsContainer = stampsContainer;
        _logger = logger;
        _templateSpecId = Environment.GetEnvironmentVariable("STAMP_TEMPLATE_SPEC_ID") ?? "";
        _deployerObjectId = Environment.GetEnvironmentVariable("DEPLOYER_OBJECT_ID") ?? "";
    }

    /// <summary>
    /// Lists all stamps from the registry.
    /// </summary>
    public async Task<List<Stamp>> ListStampsAsync()
    {
        var stamps = new List<Stamp>();
        var query = new QueryDefinition("SELECT * FROM c ORDER BY c.createdAt DESC");
        using var iterator = _stampsContainer.GetItemQueryIterator<Stamp>(query);
        while (iterator.HasMoreResults)
            stamps.AddRange(await iterator.ReadNextAsync());
        return stamps;
    }

    /// <summary>
    /// Gets a single stamp by ID.
    /// </summary>
    public async Task<Stamp?> GetStampAsync(string stampId)
    {
        try
        {
            var response = await _stampsContainer.ReadItemAsync<Stamp>(stampId, new PartitionKey(stampId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Provisions a new stamp by deploying the main.bicep template to a new resource group.
    /// </summary>
    public async Task<Stamp> ProvisionStampAsync(CreateStampRequest request, string subscriptionId)
    {
        var stampId = $"{request.Prefix}-{request.Suffix}";
        var resourceGroupName = $"{request.Prefix}-{request.Suffix}";

        var stamp = new Stamp
        {
            Id = stampId,
            StampId = stampId,
            Name = request.Name,
            Description = request.Description ?? "",
            Prefix = request.Prefix,
            Suffix = request.Suffix,
            ResourceGroup = resourceGroupName,
            SubscriptionId = subscriptionId,
            Location = request.Location,
            ConversationMode = request.ConversationMode,
            AuthMode = request.AuthMode,
            Status = StampStatus.Provisioning,
        };

        // Save stamp record first
        await _stampsContainer.UpsertItemAsync(stamp, new PartitionKey(stampId));

        try
        {
            // Create resource group
            var subscription = _armClient.GetSubscriptionResource(
                new Azure.Core.ResourceIdentifier($"/subscriptions/{subscriptionId}"));
            var rgCollection = subscription.GetResourceGroups();

            var rgData = new ResourceGroupData(request.Location);
            rgData.Tags.Add("project", "discovery-bot-v2");
            rgData.Tags.Add("stamp", stampId);
            rgData.Tags.Add("managedBy", "management-plane");

            var rgResult = await rgCollection.CreateOrUpdateAsync(
                Azure.WaitUntil.Completed,
                resourceGroupName,
                rgData);

            _logger.LogInformation("Resource group created: {ResourceGroup}", resourceGroupName);

            // Deploy via Template Spec (compiled ARM JSON published to Azure)
            if (string.IsNullOrEmpty(_templateSpecId))
                throw new InvalidOperationException("STAMP_TEMPLATE_SPEC_ID env var is required for stamp provisioning");

            var deploymentContent = new ArmDeploymentContent(new ArmDeploymentProperties(ArmDeploymentMode.Incremental)
            {
                TemplateLink = new ArmDeploymentTemplateLink
                {
                    Id = _templateSpecId,
                },
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    prefix = new { value = request.Prefix },
                    suffix = new { value = request.Suffix },
                    location = new { value = request.Location },
                    deployerObjectId = new { value = _deployerObjectId },
                    conversationMode = new { value = ToBicepValue(request.ConversationMode) },
                    authMode = new { value = ToBicepValue(request.AuthMode) },
                    enableObservability = new { value = false },
                    tags = new
                    {
                        value = new Dictionary<string, string>
                        {
                            ["project"] = "discovery-bot-v2",
                            ["stamp"] = stampId,
                            ["client"] = request.Name,
                            ["managedBy"] = "management-plane",
                        }
                    },
                }),
            });

            var deployments = rgResult.Value.GetArmDeployments();
            var deploymentResult = await deployments.CreateOrUpdateAsync(
                Azure.WaitUntil.Started, // Don't wait — this takes minutes
                $"stamp-{stampId}-{DateTime.UtcNow:yyyyMMddHHmm}",
                deploymentContent);

            _logger.LogInformation(
                "Deployment initiated for stamp {StampId}: {DeploymentId}",
                stampId, deploymentResult.Value.Id);

            // Update stamp with provisioning status
            stamp = stamp with { Status = StampStatus.Provisioning };
            await _stampsContainer.UpsertItemAsync(stamp, new PartitionKey(stampId));

            return stamp;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision stamp: {StampId}", stampId);
            stamp = stamp with { Status = StampStatus.Failed, LastError = ex.Message };
            await _stampsContainer.UpsertItemAsync(stamp, new PartitionKey(stampId));
            return stamp;
        }
    }

    /// <summary>
    /// Updates stamp status (e.g., after health check or deployment completion).
    /// </summary>
    public async Task UpdateStampAsync(Stamp stamp)
    {
        await _stampsContainer.UpsertItemAsync(stamp, new PartitionKey(stamp.StampId));
    }

    /// <summary>
    /// Pauses a stamp by scaling its ACA to 0 min replicas.
    /// </summary>
    public async Task<Stamp> PauseStampAsync(string stampId)
    {
        var stamp = await GetStampAsync(stampId);
        if (stamp is null) throw new KeyNotFoundException($"Stamp not found: {stampId}");

        // TODO: Use ArmClient to update the ACA min replicas to 0
        _logger.LogInformation("Pausing stamp: {StampId}", stampId);

        stamp = stamp with { Status = StampStatus.Paused };
        await _stampsContainer.UpsertItemAsync(stamp, new PartitionKey(stampId));
        return stamp;
    }

    /// <summary>
    /// Resumes a paused stamp.
    /// </summary>
    public async Task<Stamp> ResumeStampAsync(string stampId)
    {
        var stamp = await GetStampAsync(stampId);
        if (stamp is null) throw new KeyNotFoundException($"Stamp not found: {stampId}");

        // TODO: Use ArmClient to restore ACA min replicas
        _logger.LogInformation("Resuming stamp: {StampId}", stampId);

        stamp = stamp with { Status = StampStatus.Active };
        await _stampsContainer.UpsertItemAsync(stamp, new PartitionKey(stampId));
        return stamp;
    }

    private static string ToBicepValue(ConversationMode mode) => mode switch
    {
        ConversationMode.Lightweight => "lightweight",
        ConversationMode.Standard => "standard",
        ConversationMode.Full => "full",
        _ => "standard",
    };

    private static string ToBicepValue(AuthMode mode) => mode switch
    {
        AuthMode.None => "none",
        AuthMode.MagicLink => "magic_link",
        AuthMode.InviteCode => "invite_code",
        AuthMode.EntraExternal => "entra_external",
        _ => "none",
    };
}
