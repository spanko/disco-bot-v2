using Azure.AI.Projects;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Handlers;
using DiscoveryAgent.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        // ── Configuration ───────────────────────────────────────────────
        var config = context.Configuration;
        var settings = config.GetSection("DiscoveryBot").Get<DiscoveryBotSettings>()
            ?? DiscoveryBotSettings.FromEnvironment();
        services.AddSingleton(settings);

        // ── Azure Credential ────────────────────────────────────────────
        // Use ManagedIdentityCredential in production for security and perf.
        // DefaultAzureCredential is fine for local dev (falls through to az login).
        var credential = context.HostingEnvironment.IsDevelopment()
            ? new DefaultAzureCredential() as Azure.Core.TokenCredential
            : new ManagedIdentityCredential() as Azure.Core.TokenCredential;

        // ── Foundry Project Client (GA SDK) ─────────────────────────────
        // This is the main entry point for agent management.
        // Endpoint format: https://<resource>.services.ai.azure.com/api/projects/<project>
        services.AddSingleton(_ =>
            new AIProjectClient(new Uri(settings.ProjectEndpoint), credential));

        // ── BYO Cosmos DB ───────────────────────────────────────────────
        // Two databases:
        //   enterprise_memory — Foundry-managed (thread-message-store, etc.)
        //   discovery — Our custom containers (knowledge-items, etc.)
        services.AddSingleton(_ =>
            new CosmosClient(settings.CosmosEndpoint, credential, new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
                },
            }));
        services.AddSingleton(sp =>
            sp.GetRequiredService<CosmosClient>().GetDatabase(settings.CosmosDatabase));

        // ── BYO AI Search ───────────────────────────────────────────────
        services.AddSingleton(_ =>
            new SearchClient(
                new Uri(settings.AiSearchEndpoint),
                settings.KnowledgeIndexName,
                credential));

        // ── BYO Blob Storage ────────────────────────────────────────────
        services.AddSingleton(_ =>
            new BlobServiceClient(new Uri(settings.StorageEndpoint), credential));

        // ── Application Services ────────────────────────────────────────
        services.AddSingleton<IAgentManager, AgentManager>();
        services.AddSingleton<IKnowledgeStore, KnowledgeStore>();
        services.AddSingleton<IKnowledgeQueryService, KnowledgeQueryService>();
        services.AddSingleton<IContextManagementService, ContextManagementService>();
        services.AddSingleton<IQuestionnaireProcessor, QuestionnaireProcessor>();
        services.AddSingleton<IUserProfileService, UserProfileService>();

        services.AddScoped<IConversationHandler, ConversationHandler>();
    })
    .Build();

// =====================================================================
// Initialize agent BEFORE accepting requests.
// =====================================================================
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

try
{
    logger.LogInformation("Ensuring agent definition exists in Foundry...");
    var agentManager = host.Services.GetRequiredService<IAgentManager>();
    await agentManager.EnsureAgentExistsAsync();
    logger.LogInformation("Agent ready: {AgentName}", agentManager.AgentName);
}
catch (Exception ex)
{
    var settings = host.Services.GetRequiredService<DiscoveryBotSettings>();
    logger.LogCritical(ex,
        "Agent initialization FAILED. Check: (1) PROJECT_ENDPOINT is correct, " +
        "(2) model deployment '{Model}' exists, (3) managed identity has Azure AI User role.",
        settings.ModelDeploymentName);
}

host.Run();
