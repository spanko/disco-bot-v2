using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;

namespace DiscoveryAgent.Handlers;

/// <summary>
/// Handles the full lifecycle of a conversation turn using the Foundry GA
/// Responses API. This replaces the v1 ConversationHandler entirely.
/// 
/// v1 flow: CreateThread → AddMessage → CreateRun → Poll → ProcessToolCalls → GetMessages
/// v2 flow: CreateConversation → CreateResponse(agent_ref, conversation, input) → output
/// 
/// The Responses API is synchronous (or streaming) — no polling loop.
/// Conversations are managed by Foundry and stored in BYO Cosmos automatically.
/// Tool calls are handled inline during response generation.
/// </summary>
public class ConversationHandler : IConversationHandler
{
    private readonly ProjectOpenAIClient _openAIClient;
    private readonly IAgentManager _agentManager;
    private readonly IKnowledgeStore _knowledgeStore;
    private readonly IUserProfileService _userProfiles;
    private readonly IContextManagementService _contextService;
    private readonly DiscoveryBotSettings _settings;
    private readonly ILogger<ConversationHandler> _logger;

    public ConversationHandler(
        ProjectOpenAIClient openAIClient,
        IAgentManager agentManager,
        IKnowledgeStore knowledgeStore,
        IUserProfileService userProfiles,
        IContextManagementService contextService,
        DiscoveryBotSettings settings,
        ILogger<ConversationHandler> logger)
    {
        _openAIClient = openAIClient;
        _agentManager = agentManager;
        _knowledgeStore = knowledgeStore;
        _userProfiles = userProfiles;
        _contextService = contextService;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ConversationResponse> HandleAsync(
        ConversationRequest request, CancellationToken ct = default)
    {
        // ─────────────────────────────────────────────────────────────
        // Step 1: Get or create conversation
        // ─────────────────────────────────────────────────────────────
        string conversationId;

        if (!string.IsNullOrEmpty(request.ConversationId))
        {
            conversationId = request.ConversationId;
            _logger.LogInformation("Resuming conversation: {ConversationId}", conversationId);
        }
        else
        {
            // Create a new conversation, optionally seeding with context
            var creationOptions = new ProjectConversationCreationOptions();

            // If a discovery context is specified, inject it as a system message
            if (!string.IsNullOrEmpty(request.ContextId))
            {
                var context = await _contextService.GetContextAsync(request.ContextId);
                if (context is not null)
                {
                    var contextMessage = BuildContextSystemMessage(context);
                    creationOptions.Items.Add(
                        ResponseItem.CreateSystemMessageItem(contextMessage));
                }
            }

            var conversation = await _openAIClient.Conversations
                .CreateProjectConversationAsync(creationOptions, ct);
            conversationId = conversation.Value.Id;

            _logger.LogInformation("Created conversation: {ConversationId} for context: {ContextId}",
                conversationId, request.ContextId ?? "default");
        }

        // ─────────────────────────────────────────────────────────────
        // Step 2: Generate response via Responses API
        // ─────────────────────────────────────────────────────────────
        // GetProjectResponsesClientForAgent binds the agent + conversation
        // so every call automatically maintains state.
        var responseClient = _openAIClient.GetProjectResponsesClientForAgent(
            _agentManager.AgentName);

        var responseOptions = new ResponseCreationOptions
        {
            AgentConversationId = conversationId,
        };

        _logger.LogInformation(
            "Creating response: Agent={Agent}, Conversation={ConversationId}, Input={InputLen} chars",
            _agentManager.AgentName, conversationId, request.Message.Length);

        var response = await responseClient.CreateResponseAsync(
            [ResponseItem.CreateUserMessageItem(request.Message)],
            responseOptions,
            ct);

        var outputText = response.Value.GetOutputText();

        _logger.LogInformation(
            "Response received: {ResponseId}, Output={OutputLen} chars, Usage=({Input}+{Output} tokens)",
            response.Value.Id,
            outputText.Length,
            response.Value.Usage?.InputTokens ?? 0,
            response.Value.Usage?.OutputTokens ?? 0);

        // ─────────────────────────────────────────────────────────────
        // Step 3: Process any function tool calls in the response
        // ─────────────────────────────────────────────────────────────
        var extractedKnowledgeIds = new List<string>();

        // The Responses API includes tool call outputs inline in the response.
        // If the agent called extract_knowledge, store_user_profile, etc.,
        // those calls are in response.Value.Output as FunctionCallOutputItem.
        // 
        // NOTE: For the GA API, function tools can be configured as
        // "auto-executed" by the service, or handled client-side.
        // The implementation below handles the client-side pattern.
        // This will be fleshed out in WP-2 when we wire up the tool definitions.

        // TODO: Iterate response.Value.Output for function call items
        // and process extract_knowledge, store_user_profile, etc.
        // For now, knowledge extraction happens post-hoc in a future pass.

        return new ConversationResponse(
            ConversationId: conversationId,
            Response: outputText,
            AgentName: _agentManager.AgentName,
            ExtractedKnowledgeIds: extractedKnowledgeIds.Count > 0 ? extractedKnowledgeIds : null
        );
    }

    private static string BuildContextSystemMessage(DiscoveryContext context) =>
        $"""
        [DISCOVERY SESSION CONFIGURATION]
        Project: {context.Name}
        Description: {context.Description}
        Mode: {context.DiscoveryMode}
        Focus Areas: {string.Join(", ", context.DiscoveryAreas)}
        Key Questions: {string.Join("; ", context.KeyQuestions)}
        Please begin the discovery session following your instructions.
        """;
}
