using Azure.Identity;
using Azure.ResourceManager;
using ManagementPlane.Models;
using ManagementPlane.Services;
using Microsoft.Azure.Cosmos;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ───────────────────────────────────────────────
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT") ?? "";
var cosmosDatabase = Environment.GetEnvironmentVariable("COSMOS_DATABASE") ?? "management";
var subscriptionId = Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID") ?? "";

// ── Azure Credential ────────────────────────────────────────────
var credential = new DefaultAzureCredential();

// ── ARM Client ──────────────────────────────────────────────────
builder.Services.AddSingleton(_ => new ArmClient(credential));

// ── Cosmos DB (stamp registry) ──────────────────────────────────
builder.Services.AddSingleton(_ =>
    new CosmosClient(cosmosEndpoint, credential, new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
        },
    }));
builder.Services.AddSingleton(sp =>
{
    var client = sp.GetRequiredService<CosmosClient>();
    var db = client.GetDatabase(cosmosDatabase);
    return db.GetContainer("stamps");
});

// ── Services ────────────────────────────────────────────────────
builder.Services.AddSingleton<StampManager>();
builder.Services.AddSingleton<FleetMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetMonitor>());
builder.Services.AddHttpClient();

var app = builder.Build();

// ── JSON options ────────────────────────────────────────────────
var jsonOpts = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
};

// =====================================================================
// Health
// =====================================================================

app.MapGet("/health", () =>
    Results.Ok(new { status = "healthy", service = "management-plane", timestamp = DateTime.UtcNow }));

// =====================================================================
// Fleet API
// =====================================================================

app.MapGet("/api/fleet/stamps", async (StampManager manager) =>
{
    var stamps = await manager.ListStampsAsync();
    return Results.Ok(stamps);
});

app.MapGet("/api/fleet/stamps/{stampId}", async (string stampId, StampManager manager) =>
{
    var stamp = await manager.GetStampAsync(stampId);
    return stamp is null ? Results.NotFound() : Results.Ok(stamp);
});

app.MapPost("/api/fleet/stamps", async (HttpRequest req, StampManager manager) =>
{
    var request = await req.ReadFromJsonAsync<CreateStampRequest>(jsonOpts);
    if (request is null || string.IsNullOrEmpty(request.Name) || string.IsNullOrEmpty(request.Prefix))
        return Results.BadRequest(new { error = "Name and prefix are required" });

    var stamp = await manager.ProvisionStampAsync(request, subscriptionId);
    return Results.Ok(stamp);
});

app.MapPost("/api/fleet/stamps/{stampId}/pause", async (string stampId, StampManager manager) =>
{
    try
    {
        var stamp = await manager.PauseStampAsync(stampId);
        return Results.Ok(stamp);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapPost("/api/fleet/stamps/{stampId}/resume", async (string stampId, StampManager manager) =>
{
    try
    {
        var stamp = await manager.ResumeStampAsync(stampId);
        return Results.Ok(stamp);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/fleet/health", async (FleetMonitor monitor) =>
{
    var health = await monitor.GetFleetHealthAsync();
    return Results.Ok(health);
});

app.Run();
