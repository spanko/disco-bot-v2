namespace DiscoveryAgent.Configuration;

/// <summary>
/// Settings for the Discovery Bot v2.
/// Supports both appsettings.json binding and environment variable fallback.
/// Environment variables follow azd convention: SCREAMING_SNAKE_CASE.
/// </summary>
public class DiscoveryBotSettings
{
    // Foundry project
    public string ProjectEndpoint { get; set; } = "";
    public string AgentName { get; set; } = "discovery-agent";
    public string ModelDeploymentName { get; set; } = "gpt-4.1";

    // BYO Cosmos DB
    public string CosmosEndpoint { get; set; } = "";
    public string CosmosDatabase { get; set; } = "discovery";

    // BYO Storage
    public string StorageEndpoint { get; set; } = "";

    // BYO AI Search
    public string AiSearchEndpoint { get; set; } = "";
    public string KnowledgeIndexName { get; set; } = "knowledge-items";

    // Agent configuration
    public string InstructionsPath { get; set; } = "config/instructions.md";

    // App Insights
    public string AppInsightsConnectionString { get; set; } = "";

    /// <summary>
    /// Build settings from environment variables (azd-style).
    /// </summary>
    public static DiscoveryBotSettings FromEnvironment() => new()
    {
        ProjectEndpoint = Env("PROJECT_ENDPOINT"),
        AgentName = Env("AGENT_NAME", "discovery-agent"),
        ModelDeploymentName = Env("MODEL_DEPLOYMENT_NAME", "gpt-4.1"),
        CosmosEndpoint = Env("COSMOS_ENDPOINT"),
        CosmosDatabase = Env("COSMOS_DATABASE", "discovery"),
        StorageEndpoint = Env("STORAGE_ENDPOINT"),
        AiSearchEndpoint = Env("AI_SEARCH_ENDPOINT"),
        KnowledgeIndexName = Env("KNOWLEDGE_INDEX_NAME", "knowledge-items"),
        InstructionsPath = Env("INSTRUCTIONS_PATH", "config/instructions.md"),
        AppInsightsConnectionString = Env("APPLICATIONINSIGHTS_CONNECTION_STRING"),
    };

    private static string Env(string key, string fallback = "")
        => Environment.GetEnvironmentVariable(key) ?? fallback;
}
