using Azure.AI.Projects;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Services;

/// <summary>
/// Manages the agent definition in Foundry Agent Service (GA).
/// 
/// Key differences from v1:
/// - No more PersistentAgentsClient — uses AIProjectClient.Agents
/// - Agents are versioned, named assets — no manual ID persistence in Cosmos
/// - CreateVersion() creates or updates the agent definition
/// - The agent is referenced by name, not ID, when creating responses
/// </summary>
public class AgentManager : IAgentManager
{
    private readonly AIProjectClient _projectClient;
    private readonly DiscoveryBotSettings _settings;
    private readonly ILogger<AgentManager> _logger;
    private bool _initialized;

    public string AgentName => _settings.AgentName;

    public AgentManager(
        AIProjectClient projectClient,
        DiscoveryBotSettings settings,
        ILogger<AgentManager> logger)
    {
        _projectClient = projectClient;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the agent definition exists as a versioned prompt agent in Foundry.
    /// Creates a new version if the agent doesn't exist or instructions have changed.
    /// 
    /// Unlike v1 (which persisted agent IDs to Cosmos and polled for readiness),
    /// the GA API treats agents as named, versioned resources. CreateVersion is
    /// idempotent — calling it with the same definition is safe.
    /// </summary>
    public async Task EnsureAgentExistsAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        var instructions = await GetInstructionsAsync();
        var toolDefinitions = BuildToolDefinitions();

        _logger.LogInformation("Creating/updating agent version: Name={Name}, Model={Model}, Instructions={Len} chars",
            _settings.AgentName, _settings.ModelDeploymentName, instructions.Length);

        try
        {
            // CreateVersion creates a new version of the named agent.
            // If the agent doesn't exist yet, it creates both the agent and version.
            // The agent is then referenced by name in Responses API calls.
            var agentVersion = await _projectClient.Agents.CreateVersionAsync(
                agentName: _settings.AgentName,
                definition: new PromptAgentDefinition(_settings.ModelDeploymentName)
                {
                    Instructions = instructions,
                    Tools = toolDefinitions,
                },
                cancellationToken: ct);

            _logger.LogInformation("Agent version created: Name={Name}, Version={Version}",
                agentVersion.Value.Name, agentVersion.Value.Version);

            _initialized = true;
        }
        catch (Azure.RequestFailedException ex)
        {
            _logger.LogError(ex, "Foundry API error: Status={Status}, Code={Code}",
                ex.Status, ex.ErrorCode);
            throw;
        }
    }

    /// <summary>
    /// Loads the system prompt from config/instructions.md.
    /// Falls back to a default prompt if the file is missing.
    /// </summary>
    public async Task<string> GetInstructionsAsync()
    {
        var path = _settings.InstructionsPath;

        if (File.Exists(path))
        {
            _logger.LogInformation("Loading instructions from {Path}", path);
            return await File.ReadAllTextAsync(path);
        }

        _logger.LogWarning("Instructions file not found at {Path}, using default prompt", path);
        return GetDefaultSystemPrompt();
    }

    /// <summary>
    /// Builds context-aware instructions by combining the base prompt
    /// with a specific discovery context configuration.
    /// </summary>
    public string BuildContextualInstructions(DiscoveryContext context)
    {
        var baseInstructions = File.Exists(_settings.InstructionsPath)
            ? File.ReadAllText(_settings.InstructionsPath)
            : GetDefaultSystemPrompt();

        return $"""
            {baseInstructions}

            ---
            ## ACTIVE DISCOVERY CONTEXT
            
            **Project**: {context.Name}
            **Description**: {context.Description}
            **Mode**: {context.DiscoveryMode}
            
            **Discovery Areas**: {string.Join(", ", context.DiscoveryAreas)}
            **Key Questions**: 
            {string.Join("\n", context.KeyQuestions.Select((q, i) => $"  {i + 1}. {q}"))}
            
            **Sensitive Areas (handle carefully)**: {string.Join(", ", context.SensitiveAreas)}
            
            **Success Criteria**:
            {string.Join("\n", context.SuccessCriteria.Select(c => $"  - {c}"))}
            
            {(string.IsNullOrEmpty(context.AgentInstructions) ? "" : $"\n**Additional Instructions**:\n{context.AgentInstructions}")}
            """;
    }

    /// <summary>
    /// Builds function tool definitions for the agent.
    /// These are the same three tools from v1, but registered through the
    /// new PromptAgentDefinition.Tools collection.
    /// </summary>
    private List<AgentToolDefinition> BuildToolDefinitions()
    {
        // TODO: Convert to AgentToolDefinition format once SDK types stabilize.
        // For now, the function tool schema is the same JSON shape as v1.
        // The Responses API handles tool calls inline — no more polling loop.
        return [];
    }

    private static string GetDefaultSystemPrompt() => """
        You are an intelligent discovery agent designed to help organizations gather,
        organize, and synthesize knowledge through natural conversation.

        Your primary purpose is to discover, understand, and document knowledge — not
        to provide answers. You ask thoughtful questions, listen actively, and help
        users articulate their knowledge in structured ways.

        At the start of every new conversation, identify the user's role by asking
        about their responsibilities, then adapt your tone, depth, and focus accordingly.

        For every significant piece of information shared, extract and categorize it
        as a fact, opinion, decision, requirement, or concern, noting confidence level
        and relationships to other captured knowledge.
        """;
}
