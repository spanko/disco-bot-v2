using Azure.AI.Projects;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using Azure.AI.Projects.Agents;

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
    private string? _resolvedAgentName;
    private string? _resolvedAgentVersion;

    public string AgentName => _settings.AgentName;
    public bool IsInitialized => _initialized;

    /// <summary>
    /// The resolved agent name after initialization (from CreateAgentVersion response).
    /// </summary>
    public string? ResolvedAgentName => _resolvedAgentName;

    /// <summary>
    /// The resolved agent version string after initialization.
    /// </summary>
    public string? ResolvedAgentVersion => _resolvedAgentVersion;

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

        _logger.LogInformation(
            "Creating/updating agent version: Name={Name}, Model={Model}, Instructions={Len} chars, Tools={ToolCount}",
            _settings.AgentName, _settings.ModelDeploymentName, instructions.Length, toolDefinitions.Count);

        try
        {
            // PromptAgentDefinition is in Azure.AI.Projects (2.0.0-beta.2).
            var definition = new PromptAgentDefinition(_settings.ModelDeploymentName)
            {
                Instructions = instructions,
            };

            foreach (var tool in toolDefinitions)
            {
                definition.Tools.Add(tool);
            }

            // Strongly-typed overload: options: new(definition) creates
            // AgentVersionCreationOptions from the PromptAgentDefinition.
            var agentVersionResult = await _projectClient.Agents.CreateAgentVersionAsync(
                agentName: _settings.AgentName,
                options: new(definition),
                cancellationToken: ct);

            var agentVersion = agentVersionResult.Value;
            _resolvedAgentName = agentVersion.Name;
            _resolvedAgentVersion = agentVersion.Version;

            _logger.LogInformation(
                "Agent version created: Name={Name}, Version={Version}, Id={Id}",
                agentVersion.Name, agentVersion.Version, agentVersion.Id);

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
    /// </summary>
    private static List<ResponseTool> BuildToolDefinitions()
    {
        return
        [
            ResponseTool.CreateFunctionTool(
                functionName: "extract_knowledge",
                functionDescription: "Extract and categorize knowledge items from the user's response. " +
                    "Call this after each substantive user message to capture facts, opinions, " +
                    "decisions, requirements, and concerns.",
                functionParameters: BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        items = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    content = new { type = "string", description = "The knowledge statement" },
                                    category = new { type = "string", @enum = new[] { "fact", "opinion", "decision", "requirement", "concern" } },
                                    confidence = new { type = "number", description = "0.0 to 1.0" },
                                    tags = new { type = "array", items = new { type = "string" } }
                                },
                                required = new[] { "content", "category", "confidence" }
                            }
                        }
                    },
                    required = new[] { "items" }
                }),
                strictModeEnabled: false
            ),

            ResponseTool.CreateFunctionTool(
                functionName: "store_user_profile",
                functionDescription: "Store or update the user's role profile based on what they've shared. " +
                    "Call this after the role discovery phase of conversation.",
                functionParameters: BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        roleName = new { type = "string", description = "The user's role title" },
                        tone = new { type = "string", @enum = new[] { "formal", "conversational", "technical" } },
                        detailLevel = new { type = "string", @enum = new[] { "executive", "detailed", "technical" } },
                        priorityTopics = new { type = "array", items = new { type = "string" } },
                        questionComplexity = new { type = "string", @enum = new[] { "high-level", "detailed", "deep-dive" } }
                    },
                    required = new[] { "roleName" }
                }),
                strictModeEnabled: false
            ),

            ResponseTool.CreateFunctionTool(
                functionName: "complete_questionnaire_section",
                functionDescription: "Mark a questionnaire section as complete and summarize findings.",
                functionParameters: BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = new
                    {
                        sectionId = new { type = "string" },
                        summary = new { type = "string" },
                        keyFindings = new { type = "array", items = new { type = "string" } }
                    },
                    required = new[] { "sectionId", "summary" }
                }),
                strictModeEnabled: false
            ),
        ];
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
