using ManagementPlane.Models;

namespace ManagementPlane.Services;

/// <summary>
/// Background service that periodically polls stamp health endpoints
/// and updates stamp status in the registry. Also detects idle stamps.
/// </summary>
public class FleetMonitor : BackgroundService
{
    private readonly StampManager _stampManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<FleetMonitor> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromMinutes(5);

    public FleetMonitor(
        StampManager stampManager,
        IHttpClientFactory httpClientFactory,
        ILogger<FleetMonitor> logger)
    {
        _stampManager = stampManager;
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
