using DiscoveryAgent.Core.Models;

namespace DiscoveryAgent.Core.Interfaces;

/// <summary>
/// Manages agent lifecycle: creation, versioning, instructions loading.
/// Implemented against Foundry GA AIProjectClient.
/// </summary>
public interface IAgentManager
{
    string AgentName { get; }
    bool IsInitialized { get; }
    Task EnsureAgentExistsAsync(CancellationToken ct = default);
    Task<string> GetInstructionsAsync();
    string BuildContextualInstructions(DiscoveryContext context);
}

/// <summary>
/// Handles the full lifecycle of a conversation turn using Responses API.
/// </summary>
public interface IConversationHandler
{
    Task<ConversationResponse> HandleAsync(ConversationRequest request, CancellationToken ct = default);
}

/// <summary>
/// Stores and retrieves knowledge items with full attribution.
/// Dual-writes to Cosmos DB (persistence) and AI Search (retrieval).
/// </summary>
public interface IKnowledgeStore
{
    Task<string> StoreAsync(KnowledgeItem item);
    Task<List<KnowledgeItem>> GetByContextAsync(string contextId);
    Task<List<KnowledgeItem>> SearchAsync(string query, string? contextId = null, int maxResults = 10);
    Task<KnowledgeProvenance?> TraceOriginAsync(string itemId, string contextId);
}

/// <summary>
/// Rich query layer for the admin/reporting experience.
/// </summary>
public interface IKnowledgeQueryService
{
    Task<List<KnowledgeItem>> GetByContextPaginatedAsync(string contextId, int skip, int take);
    Task<List<KnowledgeItem>> SearchSemanticAsync(string query, string? contextId, int maxResults);
    Task<Dictionary<KnowledgeCategory, int>> GetCategorySummaryAsync(string contextId);
    Task<List<KnowledgeItem>> GetContradictionsAsync(string contextId);
    Task<List<KnowledgeItem>> GetByUserAsync(string contextId, string userId);
    Task<byte[]> ExportAsync(string contextId, string format);
}

/// <summary>
/// Manages discovery contexts — the project-level configuration.
/// </summary>
public interface IContextManagementService
{
    Task<DiscoveryContext?> GetContextAsync(string contextId);
    Task UpdateContextAsync(DiscoveryContextConfig config);
    Task<List<DiscoveryContext>> ListContextsAsync();
}

/// <summary>
/// Processes uploaded questionnaire documents into structured sessions.
/// </summary>
public interface IQuestionnaireProcessor
{
    Task<ParsedQuestionnaire> ParseAsync(Stream documentStream, string fileName);
    Task<ParsedQuestionnaire?> GetAsync(string questionnaireId);
    Task AssignToContextAsync(string questionnaireId, string contextId);
}

/// <summary>
/// Manages user profile capture and retrieval.
/// </summary>
public interface IUserProfileService
{
    Task UpsertAsync(UserProfile profile);
    Task<UserProfile?> GetAsync(string userId);
}
