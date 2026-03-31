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
    public string ModelDeploymentName { get; set; } = "gpt-4o";

    // BYO Cosmos DB
    public string CosmosEndpoint { get; set; } = "";
    public string CosmosDatabase { get; set; } = "discovery";

    // BYO Storage
    public string StorageEndpoint { get; set; } = "";

    // BYO AI Search
    public string AiSearchEndpoint { get; set; } = "";
    public string KnowledgeIndexName { get; set; } = "knowledge-items";

    // Conversation mode: lightweight | standard | full
    public string ConversationMode { get; set; } = "standard";

    // Auth: none | magic_link | invite_code | entra_external
    public string AuthMode { get; set; } = "none";
    public string JwtSigningKey { get; set; } = "";
    public string EntraTenantId { get; set; } = "";
    public string EntraClientId { get; set; } = "";
    public int MagicLinkExpiryHours { get; set; } = 24;

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
        ModelDeploymentName = Env("MODEL_DEPLOYMENT_NAME", "gpt-4o"),
        ConversationMode = Env("CONVERSATION_MODE", "standard"),
        CosmosEndpoint = Env("COSMOS_ENDPOINT"),
        CosmosDatabase = Env("COSMOS_DATABASE", "discovery"),
        StorageEndpoint = Env("STORAGE_ENDPOINT"),
        AiSearchEndpoint = Env("AI_SEARCH_ENDPOINT"),
        KnowledgeIndexName = Env("KNOWLEDGE_INDEX_NAME", "knowledge-items"),
        AuthMode = Env("AUTH_MODE", "none"),
        JwtSigningKey = Env("JWT_SIGNING_KEY"),
        EntraTenantId = Env("ENTRA_TENANT_ID"),
        EntraClientId = Env("ENTRA_CLIENT_ID"),
        MagicLinkExpiryHours = int.TryParse(Env("MAGIC_LINK_EXPIRY_HOURS", "24"), out var h) ? h : 24,
        InstructionsPath = Env("INSTRUCTIONS_PATH", "config/instructions.md"),
        AppInsightsConnectionString = Env("APPLICATIONINSIGHTS_CONNECTION_STRING"),
    };

    /// <summary>
    /// Validates that all required environment variables are set.
    /// Call after FromEnvironment() to fail fast with a clear message.
    /// </summary>
    public bool IsLightweight => ConversationMode.Equals("lightweight", StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrEmpty(ProjectEndpoint)) missing.Add("PROJECT_ENDPOINT");

        if (!IsLightweight)
        {
            if (string.IsNullOrEmpty(CosmosEndpoint)) missing.Add("COSMOS_ENDPOINT");
            if (string.IsNullOrEmpty(StorageEndpoint)) missing.Add("STORAGE_ENDPOINT");
            if (string.IsNullOrEmpty(AiSearchEndpoint)) missing.Add("AI_SEARCH_ENDPOINT");
        }

        if (AuthMode is "magic_link" && string.IsNullOrEmpty(JwtSigningKey))
            missing.Add("JWT_SIGNING_KEY (required for magic_link auth)");
        if (AuthMode is "entra_external")
        {
            if (string.IsNullOrEmpty(EntraTenantId)) missing.Add("ENTRA_TENANT_ID");
            if (string.IsNullOrEmpty(EntraClientId)) missing.Add("ENTRA_CLIENT_ID");
        }

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Missing required environment variables for '{ConversationMode}' mode, '{AuthMode}' auth: {string.Join(", ", missing)}");
    }

    private static string Env(string key, string fallback = "")
        => Environment.GetEnvironmentVariable(key) ?? fallback;
}
