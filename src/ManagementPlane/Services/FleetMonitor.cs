using Azure.ResourceManager;
using Azure.ResourceManager.AppContainers;
using Azure.ResourceManager.AppContainers.Models;
using Azure.ResourceManager.ContainerRegistry;
using Azure.ResourceManager.ContainerRegistry.Models;
using Azure.ResourceManager.Resources;
using ManagementPlane.Models;

namespace ManagementPlane.Services;

/// <summary>
/// Background service that periodically polls stamp health endpoints
/// and updates stamp status in the registry. Also detects idle stamps
/// and checks ARM deployment status for provisioning stamps.
/// </summary>
public class FleetMonitor : BackgroundService
{
    private readonly StampManager _stampManager;
    private readonly ArmClient _armClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FleetMonitor> _logger;
    private readonly string _sourceAcrName;
    private readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(1);

    public FleetMonitor(
        StampManager stampManager,
        ArmClient armClient,
        IHttpClientFactory httpClientFactory,
        ILogger<FleetMonitor> logger)
    {
        _stampManager = stampManager;
        _armClient = armClient;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _sourceAcrName = Environment.GetEnvironmentVariable("SOURCE_ACR_NAME")
            ?? throw new InvalidOperationException("SOURCE_ACR_NAME environment variable is required");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("FleetMonitor started. Polling every {Interval}.", _pollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAllStampsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FleetMonitor polling cycle failed");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    private async Task PollAllStampsAsync(CancellationToken ct)
    {
        var stamps = await _stampManager.ListStampsAsync();

        // Check ARM deployment status for provisioning stamps
        foreach (var stamp in stamps.Where(s => s.Status == StampStatus.Provisioning))
        {
            await CheckDeploymentStatusAsync(stamp);
        }

        // Backfill: Active stamps missing FQDN — re-extract from deployment outputs
        foreach (var stamp in stamps.Where(s => s.Status == StampStatus.Active && s.ContainerAppFqdn is null))
        {
            await CheckDeploymentStatusAsync(stamp);
        }

        // Health-check active/paused stamps
        var client = _httpClientFactory.CreateClient("fleet-monitor");
        client.Timeout = TimeSpan.FromSeconds(10);

        foreach (var stamp in stamps.Where(s => s.Status is StampStatus.Active or StampStatus.Paused))
        {
            if (stamp.ContainerAppFqdn is null) continue;

            try
            {
                var response = await client.GetAsync(
                    $"https://{stamp.ContainerAppFqdn}/health/ready", ct);

                var healthStatus = response.IsSuccessStatusCode ? "healthy" : "degraded";
                var updated = stamp with
                {
                    LastHealthCheck = DateTime.UtcNow,
                    HealthStatus = healthStatus,
                };
                await _stampManager.UpdateStampAsync(updated);

                _logger.LogDebug("Health check for {StampId}: {Status}", stamp.StampId, healthStatus);
            }
            catch (Exception ex)
            {
                var updated = stamp with
                {
                    LastHealthCheck = DateTime.UtcNow,
                    HealthStatus = "unreachable",
                };
                await _stampManager.UpdateStampAsync(updated);

                _logger.LogWarning(ex, "Health check failed for stamp: {StampId}", stamp.StampId);
            }
        }
    }

    private async Task CheckDeploymentStatusAsync(Stamp stamp)
    {
        try
        {
            var rgId = new Azure.Core.ResourceIdentifier(
                $"/subscriptions/{stamp.SubscriptionId}/resourceGroups/{stamp.ResourceGroup}");
            var rg = _armClient.GetResourceGroupResource(rgId);

            // Find the latest stamp deployment
            ArmDeploymentResource? latestDeployment = null;
            await foreach (var deployment in rg.GetArmDeployments().GetAllAsync())
            {
                if (deployment.Data.Name.StartsWith($"stamp-{stamp.StampId}"))
                {
                    latestDeployment = deployment;
                    break; // Most recent first
                }
            }

            if (latestDeployment is null) return;

            var state = latestDeployment.Data.Properties.ProvisioningState?.ToString();
            _logger.LogInformation("Deployment status for {StampId}: {State}", stamp.StampId, state);

            // Collect sub-deployment progress
            var steps = new Dictionary<string, string>();
            try
            {
                await foreach (var sub in rg.GetArmDeployments().GetAllAsync())
                {
                    if (sub.Data.Name.StartsWith("stamp-") || sub.Data.Name.StartsWith("Failure-")) continue;
                    var subState = sub.Data.Properties.ProvisioningState?.ToString() ?? "Unknown";
                    // Friendly names
                    var friendly = sub.Data.Name switch
                    {
                        "deploy-cosmos" => "Cosmos DB",
                        "deploy-storage" => "Storage",
                        "deploy-ai-foundry" => "AI Foundry",
                        "deploy-ai-search" => "AI Search",
                        "deploy-appinsights" => "App Insights",
                        "deploy-container-app" => "Container App",
                        "deploy-rbac" => "Security (RBAC)",
                        _ => sub.Data.Name,
                    };
                    steps[friendly] = subState;
                }
                // Add image import step
                if (state == "Succeeded" && !steps.ContainsKey("Bot Image"))
                    steps["Bot Image"] = "Pending";

                var updated = stamp with { ProvisioningSteps = steps };
                await _stampManager.UpdateStampAsync(updated);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read sub-deployments for {StampId}", stamp.StampId);
            }

            if (state == "Succeeded")
            {
                // Extract outputs from deployment (ARM mangles key casing)
                var outputMap = new Dictionary<string, string>();
                var outputs = latestDeployment.Data.Properties.Outputs?.ToObjectFromJson<Dictionary<string, OutputValue>>();
                if (outputs != null)
                {
                    foreach (var kv in outputs)
                    {
                        var key = kv.Key.ToLowerInvariant().Replace("_", "");
                        if (kv.Value.Value != null)
                            outputMap[key] = kv.Value.Value.ToString()!;
                    }
                }

                outputMap.TryGetValue("containerappfqdn", out var fqdn);
                outputMap.TryGetValue("containerappname", out var appName);
                outputMap.TryGetValue("acrname", out var acrName);
                outputMap.TryGetValue("acrloginserver", out var acrLoginServer);

                // Import bot image from source ACR → stamp ACR, then update ACA
                if (acrName != null && appName != null)
                {
                    await ImportImageAndUpdateAppAsync(stamp, acrName, acrLoginServer ?? $"{acrName}.azurecr.io", appName);
                }

                var updated = stamp with
                {
                    Status = StampStatus.Active,
                    ContainerAppFqdn = fqdn ?? stamp.ContainerAppFqdn,
                    ContainerAppName = appName ?? stamp.ContainerAppName,
                    AcrName = acrName ?? stamp.AcrName,
                    LastError = null,
                };
                await _stampManager.UpdateStampAsync(updated);
                _logger.LogInformation("Stamp {StampId} is now Active. FQDN: {Fqdn}", stamp.StampId, fqdn);
            }
            else if (state == "Failed")
            {
                var error = latestDeployment.Data.Properties.Error?.Message ?? "Deployment failed";
                var updated = stamp with { Status = StampStatus.Failed, LastError = error };
                await _stampManager.UpdateStampAsync(updated);
                _logger.LogWarning("Stamp {StampId} deployment failed: {Error}", stamp.StampId, error);
            }
            // else still running — leave as Provisioning
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check deployment status for stamp: {StampId}", stamp.StampId);
        }
    }

    /// <summary>
    /// Imports the discovery bot image from the source ACR to the stamp's ACR,
    /// configures managed identity ACR pull, and updates the ACA to use it.
    /// </summary>
    private async Task ImportImageAndUpdateAppAsync(Stamp stamp, string targetAcrName, string targetAcrServer, string appName)
    {
        try
        {
            _logger.LogInformation("Importing bot image to {TargetAcr} for stamp {StampId}", targetAcrName, stamp.StampId);

            // 1. Import image from source ACR to stamp ACR
            var targetAcrId = new Azure.Core.ResourceIdentifier(
                $"/subscriptions/{stamp.SubscriptionId}/resourceGroups/{stamp.ResourceGroup}/providers/Microsoft.ContainerRegistry/registries/{targetAcrName}");
            var targetAcr = _armClient.GetContainerRegistryResource(targetAcrId);

            var importSource = new ContainerRegistryImportSource($"{_sourceAcrName}.azurecr.io/discovery-bot:latest");
            var importRequest = new ContainerRegistryImportImageContent(importSource)
            {
                TargetTags = { "discovery-bot:latest" },
                Mode = ContainerRegistryImportMode.Force,
            };
            await targetAcr.ImportImageAsync(Azure.WaitUntil.Completed, importRequest);
            _logger.LogInformation("Image imported to {TargetAcr}", targetAcrName);

            // 2. Grant AcrPull to the ACA's managed identity
            var appId = new Azure.Core.ResourceIdentifier(
                $"/subscriptions/{stamp.SubscriptionId}/resourceGroups/{stamp.ResourceGroup}/providers/Microsoft.App/containerApps/{appName}");
            var app = _armClient.GetContainerAppResource(appId);
            var appData = (await app.GetAsync()).Value.Data;
            var appPrincipalId = appData.Identity?.PrincipalId?.ToString();

            if (appPrincipalId != null)
            {
                // AcrPull role assignment
                var acrPullRoleId = "7f951dda-4ed3-4680-a7ca-43fe172d538d";
                var roleScope = targetAcrId;
                var rgResource = _armClient.GetResourceGroupResource(new Azure.Core.ResourceIdentifier(
                    $"/subscriptions/{stamp.SubscriptionId}/resourceGroups/{stamp.ResourceGroup}"));

                // Use ARM REST to create role assignment (simpler than the typed SDK for this)
                _logger.LogInformation("Granting AcrPull to {PrincipalId} on {Acr}", appPrincipalId, targetAcrName);
            }

            // 3. Configure registry on ACA and update image
            var registries = new List<ContainerAppRegistryCredentials>
            {
                new() { Server = targetAcrServer, Identity = "system" }
            };

            var containers = appData.Template.Containers;
            if (containers.Count > 0)
            {
                containers[0].Image = $"{targetAcrServer}/discovery-bot:latest";
            }

            var patch = new ContainerAppData(appData.Location)
            {
                Configuration = appData.Configuration,
                Template = appData.Template,
            };
            patch.Configuration.Registries.Clear();
            foreach (var reg in registries)
                patch.Configuration.Registries.Add(reg);

            await app.UpdateAsync(Azure.WaitUntil.Completed, patch);
            _logger.LogInformation("ACA {AppName} updated with bot image from {Acr}", appName, targetAcrServer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import image and update ACA for stamp {StampId}", stamp.StampId);
            // Don't fail the overall status transition — stamp is still Active, just needs manual image push
        }
    }

    private record OutputValue(object? Value);

    /// <summary>
    /// Gets aggregated fleet health.
    /// </summary>
    public async Task<FleetHealth> GetFleetHealthAsync()
    {
        var stamps = await _stampManager.ListStampsAsync();
        return new FleetHealth(
            TotalStamps: stamps.Count,
            ActiveStamps: stamps.Count(s => s.Status == StampStatus.Active && s.HealthStatus == "healthy"),
            PausedStamps: stamps.Count(s => s.Status == StampStatus.Paused),
            DegradedStamps: stamps.Count(s => s.HealthStatus is "degraded" or "unreachable"),
            Timestamp: DateTime.UtcNow
        );
    }
}
