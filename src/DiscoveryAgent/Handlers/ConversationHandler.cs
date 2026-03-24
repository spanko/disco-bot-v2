using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using DiscoveryAgent.Services;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using System.Text.Json;

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
    private readonly AIProjectClient _projectClient;
    private readonly IAgentManager _agentManager;
    private readonly IKnowledgeStore _knowledgeStore;
    private readonly IUserProfileService _userProfiles;
    private readonly IContextManagementService _contextService;
    private readonly DiscoveryBotSettings _settings;
    private readonly ILogger<ConversationHandler> _logger;

    public ConversationHandler(
        AIProjectClient projectClient,
        IAgentManager agentManager,
        IKnowledgeStore knowledgeStore,
        IUserProfileService userProfiles,
        IContextManagementService contextService,
        DiscoveryBotSettings settings,
        ILogger<ConversationHandler> logger)
    {
        _projectClient = projectClient;
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

            var conversation = await _projectClient.OpenAI.Conversations
                .CreateProjectConversationAsync(creationOptions, ct);
            conversationId = conversation.Value.Id;

            _logger.LogInformation("Created conversation: {ConversationId} for context: {ContextId}",
                conversationId, request.ContextId ?? "default");
        }

        // ─────────────────────────────────────────────────────────────
        // Step 2: Generate response via Responses API
        // ─────────────────────────────────────────────────────────────
        // Bind BOTH agent and conversation at client level — the model
        // comes from the agent definition, conversation is automatic.
        var responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(
            defaultAgent: _agentManager.AgentName,
            defaultConversationId: conversationId);

        _logger.LogInformation(
            "Creating response: Agent={Agent}, Conversation={ConversationId}, Input={InputLen} chars",
            _agentManager.AgentName, conversationId, request.Message.Length);

        var responseOptions = new CreateResponseOptions();
        responseOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(request.Message));

        var response = await responseClient.CreateResponseAsync(
            responseOptions, ct);

        // ─────────────────────────────────────────────────────────────
        // Step 3: Process function tool calls in a loop
        // ─────────────────────────────────────────────────────────────
        // The Responses API returns function_call items in the output
        // when the agent wants to invoke a tool. We execute the function
        // locally, submit the result, and re-call until no more tool
        // calls remain. This replaces v1's polling loop entirely.
        var extractedKnowledgeIds = new List<string>();
        var currentResponse = response.Value;

        while (HasFunctionCalls(currentResponse))
        {
            var toolOutputs = new List<ResponseItem>();

            // Collect all output items (including function calls) as input for next round
            foreach (var item in currentResponse.OutputItems)
            {
                toolOutputs.Add(item);

                if (item is FunctionCallResponseItem functionCall)
                {
                    _logger.LogInformation("Processing tool call: {Function}", functionCall.FunctionName);

                    var result = await ExecuteFunctionAsync(
                        functionCall, request.UserId, conversationId, request.ContextId);

                    toolOutputs.Add(
                        ResponseItem.CreateFunctionCallOutputItem(
                            functionCall.CallId,
                            result.Output));

                    if (result.KnowledgeIds is not null)
                        extractedKnowledgeIds.AddRange(result.KnowledgeIds);
                }
            }

            // Submit tool results — conversation is already bound at client level.
            // Use parameterless constructor + PreviousResponseId for chaining.
            var followUpOptions = new CreateResponseOptions
            {
                PreviousResponseId = currentResponse.Id,
            };
            foreach (var output in toolOutputs)
                followUpOptions.InputItems.Add(output);

            var nextResponse = await responseClient.CreateResponseAsync(
                followUpOptions, ct);

            currentResponse = nextResponse.Value;

            _logger.LogInformation(
                "Follow-up response: {ResponseId}, Output={OutputLen} chars",
                currentResponse.Id, currentResponse.GetOutputText().Length);
        }

        var outputText = currentResponse.GetOutputText();

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

    // ─── Function dispatch ───────────────────────────────────────────

    private static bool HasFunctionCalls(ResponseResult response) =>
        response.OutputItems.Any(item => item is FunctionCallResponseItem);

    private async Task<ToolCallResult> ExecuteFunctionAsync(
        FunctionCallResponseItem functionCall,
        string userId,
        string conversationId,
        string? contextId)
    {
        try
        {
            return functionCall.FunctionName switch
            {
                "extract_knowledge" => await HandleKnowledgeExtraction(
                    functionCall.FunctionArguments.ToString(), userId, conversationId, contextId ?? "default"),

                "store_user_profile" => await HandleProfileUpdate(
                    functionCall.FunctionArguments.ToString(), userId),

                "complete_questionnaire_section" => HandleSectionComplete(
                    functionCall.FunctionArguments.ToString()),

                _ => new ToolCallResult(
                    JsonSerializer.Serialize(new { error = $"Unknown function: {functionCall.FunctionName}" }),
                    null)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool call failed: {Function}", functionCall.FunctionName);
            return new ToolCallResult(
                JsonSerializer.Serialize(new { error = ex.Message }),
                null);
        }
    }

    private async Task<ToolCallResult> HandleKnowledgeExtraction(
        string arguments, string userId, string conversationId, string contextId)
    {
        var args = JsonSerializer.Deserialize<KnowledgeExtractionArgs>(arguments,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (args?.Items is null) return new("{}", null);

        var ids = new List<string>();
        foreach (var item in args.Items)
        {
            var ki = new KnowledgeItem
            {
                Content = item.Content,
                Category = Enum.Parse<KnowledgeCategory>(item.Category, true),
                Confidence = item.Confidence,
                SourceUserId = userId,
                SourceConversationId = conversationId,
                RelatedContextId = contextId,
                Tags = item.Tags ?? [],
            };

            var storedId = await _knowledgeStore.StoreAsync(ki);
            ids.Add(storedId);
        }

        return new(
            JsonSerializer.Serialize(new { stored = ids.Count, ids }),
            ids);
    }

    private async Task<ToolCallResult> HandleProfileUpdate(string arguments, string userId)
    {
        var args = JsonSerializer.Deserialize<ProfileUpdateArgs>(arguments,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (args is null) return new("{}", null);

        var profile = new UserProfile
        {
            UserId = userId,
            RoleName = args.RoleName,
            Tone = Enum.TryParse<CommunicationTone>(args.Tone, true, out var t) ? t : CommunicationTone.Conversational,
            DetailLevel = Enum.TryParse<DetailLevel>(args.DetailLevel, true, out var d) ? d : DetailLevel.Detailed,
            PriorityTopics = args.PriorityTopics ?? [],
            QuestionComplexity = Enum.TryParse<QuestionComplexity>(
                args.QuestionComplexity?.Replace("-", ""), true, out var q) ? q : QuestionComplexity.Detailed,
        };

        await _userProfiles.UpsertAsync(profile);

        return new(
            JsonSerializer.Serialize(new { status = "profile_updated", role = args.RoleName }),
            null);
    }

    private static ToolCallResult HandleSectionComplete(string arguments) =>
        new(JsonSerializer.Serialize(new { status = "section_completed" }), null);

    // ─── Deserialization helpers ─────────────────────────────────────

    private record ToolCallResult(string Output, List<string>? KnowledgeIds);
    private record KnowledgeExtractionArgs(List<KnowledgeItemArg>? Items);
    private record KnowledgeItemArg(string Content, string Category, double Confidence, List<string>? Tags);
    private record ProfileUpdateArgs(string RoleName, string? Tone, string? DetailLevel, List<string>? PriorityTopics, string? QuestionComplexity);
}
