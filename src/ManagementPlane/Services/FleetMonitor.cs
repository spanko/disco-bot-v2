using Azure.ResourceManager;
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

            if (state == "Succeeded")
            {
                // Extract FQDN from deployment outputs (ARM mangles key casing)
                string? fqdn = null;
                string? appName = null;
                var outputs = latestDeployment.Data.Properties.Outputs?.ToObjectFromJson<Dictionary<string, OutputValue>>();
                if (outputs != null)
                {
                    // ARM output keys have inconsistent casing — search case-insensitively
                    foreach (var kv in outputs)
                    {
                        var key = kv.Key.ToLowerInvariant().Replace("_", "");
                        if (key == "containerappfqdn")
                            fqdn = kv.Value.Value?.ToString();
                        else if (key == "containerappname")
                            appName = kv.Value.Value?.ToString();
                    }
                }

                var updated = stamp with
                {
                    Status = StampStatus.Active,
                    ContainerAppFqdn = fqdn ?? stamp.ContainerAppFqdn,
                    ContainerAppName = appName ?? stamp.ContainerAppName,
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
