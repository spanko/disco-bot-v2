using System.Text.Json.Serialization;

namespace ManagementPlane.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StampStatus { Provisioning, Active, Paused, Failed, Deprovisioning }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConversationMode { Lightweight, Standard, Full }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthMode { None, MagicLink, InviteCode, EntraExternal }

/// <summary>
/// Represents a deployed Discovery Bot stamp (one per client).
/// </summary>
public record Stamp
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string StampId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";

    // Deployment config
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";
    public string ResourceGroup { get; init; } = "";
    public string SubscriptionId { get; init; } = "";
    public string Location { get; init; } = "eastus2";
    public ConversationMode ConversationMode { get; init; } = ConversationMode.Standard;
    public AuthMode AuthMode { get; init; } = AuthMode.None;

    // Runtime info (populated after deployment)
    public string? ContainerAppFqdn { get; init; }
    public string? AcrName { get; init; }
    public string? ContainerAppName { get; init; }

    // Status
    public StampStatus Status { get; init; } = StampStatus.Provisioning;
    public string? LastError { get; init; }
    public Dictionary<string, string>? ProvisioningSteps { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? LastHealthCheck { get; init; }
    public string? HealthStatus { get; init; }
}

/// <summary>
/// Request to provision a new stamp.
/// </summary>
public record CreateStampRequest(
    string Name,
    string Prefix,
    string Suffix,
    string? Description = null,
    string Location = "eastus2",
    ConversationMode ConversationMode = ConversationMode.Standard,
    AuthMode AuthMode = AuthMode.None
);

/// <summary>
/// Aggregated fleet health view.
/// </summary>
public record FleetHealth(
    int TotalStamps,
    int ActiveStamps,
    int PausedStamps,
    int DegradedStamps,
    DateTime Timestamp
);

/// <summary>
/// Partial update request for an existing stamp.
/// Only non-null fields are applied.
/// </summary>
public record UpdateStampRequest(
    string? Name = null,
    string? Description = null,
    string? ContainerAppFqdn = null,
    string? AcrName = null,
    string? ContainerAppName = null,
    StampStatus? Status = null,
    ConversationMode? ConversationMode = null,
    AuthMode? AuthMode = null,
    string? LastError = null
);

/// <summary>
/// Cost data for a stamp.
/// </summary>
public record StampCost(
    string StampId,
    string StampName,
    decimal TotalCost,
    string Currency,
    string Period,
    Dictionary<string, decimal> ByResource
);
