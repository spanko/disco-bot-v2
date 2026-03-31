using Azure.AI.Projects;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using DiscoveryAgent.Telemetry;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DiscoveryAgent.Handlers;

/// <summary>
/// Lightweight conversation handler that uses NO Foundry-managed conversations
/// and NO BYO Cosmos. Instead, it:
///
/// 1. Uses an agent-only ProjectResponsesClient (no conversation binding)
/// 2. Maintains an in-memory history keyed by a generated conversation ID
/// 3. Re-sends relevant history as InputItems on each turn
///
/// This enables zero-cost operation — only the Foundry API call is billable.
/// Trade-offs: history is lost on container restart, no cross-replica continuity.
/// </summary>
public class LightweightConversationHandler : IConversationHandler
{
    private readonly AIProjectClient _projectClient;
    private readonly IAgentManager _agentManager;
    private readonly IKnowledgeStore _knowledgeStore;
    private readonly IUserProfileService _userProfiles;
    private readonly DiscoveryBotSettings _settings;
    private readonly ILogger<LightweightConversationHandler> _logger;

    // In-memory conversation history. Keys are generated conversation IDs.
    // ConcurrentDictionary for thread safety across concurrent requests.
    private static readonly ConcurrentDictionary<string, List<ResponseItem>> _histories = new();

    public LightweightConversationHandler(
        AIProjectClient projectClient,
        IAgentManager agentManager,
        IKnowledgeStore knowledgeStore,
        IUserProfileService userProfiles,
        DiscoveryBotSettings settings,
        ILogger<LightweightConversationHandler> logger)
    {
        _projectClient = projectClient;
        _agentManager = agentManager;
        _knowledgeStore = knowledgeStore;
        _userProfiles = userProfiles;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ConversationResponse> HandleAsync(
        ConversationRequest request, CancellationToken ct = default)
    {
        // ─────────────────────────────────────────────────────────────
        // Step 1: Get or create conversation (in-memory only)
        // ─────────────────────────────────────────────────────────────
        string conversationId;

        if (!string.IsNullOrEmpty(request.ConversationId))
        {
            conversationId = request.ConversationId;
            DiscoveryMetrics.ConversationsResumed.Add(1);
            _logger.LogInformation("Resuming lightweight conversation: {ConversationId}", conversationId);
        }
        else
        {
            conversationId = $"lw-{Guid.NewGuid():N}";
            _histories[conversationId] = [];
            DiscoveryMetrics.ConversationsCreated.Add(1,
                new KeyValuePair<string, object?>("contextId", request.ContextId ?? "default"));
            _logger.LogInformation("Created lightweight conversation: {ConversationId}", conversationId);
        }

        var history = _histories.GetOrAdd(conversationId, _ => []);

        // ─────────────────────────────────────────────────────────────
        // Step 2: Build input with history + new message
        // ─────────────────────────────────────────────────────────────
        var responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(
            defaultAgent: _agentManager.AgentName);

        var responseOptions = new CreateResponseOptions();

        // Re-send prior history
        foreach (var item in history)
            responseOptions.InputItems.Add(item);

        // Add new user message
        var userItem = ResponseItem.CreateUserMessageItem(request.Message);
        responseOptions.InputItems.Add(userItem);

        _logger.LogInformation(
            "Creating lightweight response: Agent={Agent}, Conversation={ConversationId}, HistoryItems={HistoryCount}, Input={InputLen} chars",
            _agentManager.AgentName, conversationId, history.Count, request.Message.Length);

        var response = await responseClient.CreateResponseAsync(responseOptions, ct);
        RecordTokenUsage(response.Value);

        // ─────────────────────────────────────────────────────────────
        // Step 3: Process function tool calls
        // ─────────────────────────────────────────────────────────────
        var extractedKnowledgeIds = new List<string>();
        var currentResponse = response.Value;

        while (HasFunctionCalls(currentResponse))
        {
            var inputItems = new List<ResponseItem>();

            foreach (var item in currentResponse.OutputItems)
            {
                inputItems.Add(item);

                if (item is FunctionCallResponseItem functionCall)
                {
                    _logger.LogInformation("Processing tool call: {Function}", functionCall.FunctionName);

                    var sw = Stopwatch.StartNew();
                    var result = await ExecuteFunctionAsync(
                        functionCall, request.UserId, conversationId, request.ContextId);
                    sw.Stop();

                    DiscoveryMetrics.ToolCallsTotal.Add(1,
                        new KeyValuePair<string, object?>("function", functionCall.FunctionName));
                    DiscoveryMetrics.ToolCallDuration.Record(sw.ElapsedMilliseconds,
                        new KeyValuePair<string, object?>("function", functionCall.FunctionName));

                    inputItems.Add(
                        ResponseItem.CreateFunctionCallOutputItem(
                            functionCall.CallId,
                            result.Output));

                    if (result.KnowledgeIds is not null)
                        extractedKnowledgeIds.AddRange(result.KnowledgeIds);
                }
            }

            var followUpOptions = new CreateResponseOptions();
            // Include full history + current turn for context
            foreach (var item in history)
                followUpOptions.InputItems.Add(item);
            followUpOptions.InputItems.Add(userItem);
            foreach (var output in inputItems)
                followUpOptions.InputItems.Add(output);

            var nextResponse = await responseClient.CreateResponseAsync(followUpOptions, ct);
            currentResponse = nextResponse.Value;
            RecordTokenUsage(currentResponse);
        }

        // ─────────────────────────────────────────────────────────────
        // Step 4: Update in-memory history
        // ─────────────────────────────────────────────────────────────
        history.Add(userItem);
        // Store the assistant's final text response in history
        var outputText = currentResponse.GetOutputText();
        history.Add(ResponseItem.CreateAssistantMessageItem(outputText));

        return new ConversationResponse(
            ConversationId: conversationId,
            Response: outputText,
            AgentName: _agentManager.AgentName,
            ExtractedKnowledgeIds: extractedKnowledgeIds.Count > 0 ? extractedKnowledgeIds : null
        );
    }

    private void RecordTokenUsage(ResponseResult response)
    {
        DiscoveryMetrics.ResponseCalls.Add(1);
        var usage = response.Usage;
        if (usage is null) return;

        DiscoveryMetrics.InputTokens.Add(usage.InputTokenCount);
        DiscoveryMetrics.OutputTokens.Add(usage.OutputTokenCount);
        DiscoveryMetrics.TotalTokens.Add(usage.TotalTokenCount);
    }

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
            DiscoveryMetrics.AgentErrors.Add(1,
                new KeyValuePair<string, object?>("function", functionCall.FunctionName));
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
            DiscoveryMetrics.ExtractionConfidence.Record(item.Confidence);
        }

        DiscoveryMetrics.KnowledgeItemsExtracted.Add(ids.Count,
            new KeyValuePair<string, object?>("contextId", contextId));

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

    private static ToolCallResult HandleSectionComplete(string arguments)
    {
        DiscoveryMetrics.SectionsCompleted.Add(1);
        return new(JsonSerializer.Serialize(new { status = "section_completed" }), null);
    }

    private record ToolCallResult(string Output, List<string>? KnowledgeIds);
    private record KnowledgeExtractionArgs(List<KnowledgeItemArg>? Items);
    private record KnowledgeItemArg(string Content, string Category, double Confidence, List<string>? Tags);
    private record ProfileUpdateArgs(string RoleName, string? Tone, string? DetailLevel, List<string>? PriorityTopics, string? QuestionComplexity);
}
