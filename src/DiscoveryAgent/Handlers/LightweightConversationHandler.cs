using Azure.AI.Projects;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using DiscoveryAgent.Telemetry;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace DiscoveryAgent.Handlers;

/// <summary>
/// Lightweight conversation handler that uses NO Foundry-managed conversations.
/// History is persisted to Cosmos DB (conversation-turns container) and rebuilt
/// as ResponseItem inputs on each turn, so sessions survive container restarts
/// and work across replicas.
/// </summary>
public class LightweightConversationHandler : IConversationHandler
{
    private readonly AIProjectClient _projectClient;
    private readonly IAgentManager _agentManager;
    private readonly IKnowledgeStore _knowledgeStore;
    private readonly IUserProfileService _userProfiles;
    private readonly IContextManagementService _contextService;
    private readonly IQuestionnaireProcessor _questionnaireProcessor;
    private readonly DiscoveryBotSettings _settings;
    private readonly ILogger<LightweightConversationHandler> _logger;
    private readonly Container? _turnsContainer;

    public LightweightConversationHandler(
        AIProjectClient projectClient,
        IAgentManager agentManager,
        IKnowledgeStore knowledgeStore,
        IUserProfileService userProfiles,
        IContextManagementService contextService,
        IQuestionnaireProcessor questionnaireProcessor,
        DiscoveryBotSettings settings,
        ILogger<LightweightConversationHandler> logger,
        Database? cosmosDb = null)
    {
        _projectClient = projectClient;
        _agentManager = agentManager;
        _knowledgeStore = knowledgeStore;
        _userProfiles = userProfiles;
        _contextService = contextService;
        _questionnaireProcessor = questionnaireProcessor;
        _settings = settings;
        _logger = logger;
        _turnsContainer = cosmosDb?.GetContainer("conversation-turns");
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
            DiscoveryMetrics.ConversationsResumed.Add(1);
            _logger.LogInformation("Resuming lightweight conversation: {ConversationId}", conversationId);
        }
        else
        {
            conversationId = $"lw-{Guid.NewGuid():N}";
            DiscoveryMetrics.ConversationsCreated.Add(1,
                new KeyValuePair<string, object?>("contextId", request.ContextId ?? "default"));
            _logger.LogInformation("Created lightweight conversation: {ConversationId}", conversationId);
        }

        // ─────────────────────────────────────────────────────────────
        // Step 2: Ensure agent exists (lazy init in lightweight mode)
        // ─────────────────────────────────────────────────────────────
        await _agentManager.EnsureAgentExistsAsync(ct);

        // ─────────────────────────────────────────────────────────────
        // Step 3: Load history from Cosmos and build input
        // ─────────────────────────────────────────────────────────────
        var responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(
            defaultAgent: _agentManager.AgentName);

        var responseOptions = new CreateResponseOptions();

        // Build context system message — inject for BOTH new and resumed conversations
        // because lightweight mode is stateless (no server-side conversation state).
        ResponseItem? contextSystemItem = null;
        var effectiveContextId = request.ContextId;

        // For resumed conversations, recover the contextId from the first stored turn
        if (!string.IsNullOrEmpty(request.ConversationId) && string.IsNullOrEmpty(effectiveContextId))
        {
            var existingTurns = await LoadTurnsAsync(conversationId);
            if (existingTurns.Count > 0)
                effectiveContextId = existingTurns[0].ContextId;
        }

        if (!string.IsNullOrEmpty(effectiveContextId))
        {
            var context = await _contextService.GetContextAsync(effectiveContextId);
            if (context is not null)
            {
                // Fetch any linked questionnaires
                var questionnaires = new List<ParsedQuestionnaire>();
                foreach (var qId in context.QuestionnaireIds)
                {
                    var q = await _questionnaireProcessor.GetAsync(qId);
                    if (q is not null) questionnaires.Add(q);
                    else _logger.LogWarning("Linked questionnaire not found: {QuestionnaireId}", qId);
                }

                var contextMessage = BuildContextSystemMessage(context, questionnaires);
                contextSystemItem = ResponseItem.CreateSystemMessageItem(contextMessage);
                responseOptions.InputItems.Add(contextSystemItem);
                _logger.LogInformation(
                    "Injected discovery context: {ContextId} with {QuestionnaireCount} questionnaire(s)",
                    effectiveContextId, questionnaires.Count);
            }
        }

        // Rebuild history from persisted turns
        var priorTurns = await LoadTurnsAsync(conversationId);
        foreach (var turn in priorTurns)
        {
            responseOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(turn.UserMessage));
            responseOptions.InputItems.Add(ResponseItem.CreateAssistantMessageItem(turn.AgentResponse));
        }

        // Add new user message
        var userItem = ResponseItem.CreateUserMessageItem(request.Message);
        responseOptions.InputItems.Add(userItem);

        _logger.LogInformation(
            "Creating lightweight response: Agent={Agent}, Conversation={ConversationId}, HistoryItems={HistoryCount}, Input={InputLen} chars",
            _agentManager.AgentName, conversationId, priorTurns.Count, request.Message.Length);

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
            // Re-inject context system message so the agent retains its instructions
            if (contextSystemItem is not null)
                followUpOptions.InputItems.Add(contextSystemItem);
            // Include full history + current turn for context
            foreach (var turn in priorTurns)
            {
                followUpOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(turn.UserMessage));
                followUpOptions.InputItems.Add(ResponseItem.CreateAssistantMessageItem(turn.AgentResponse));
            }
            followUpOptions.InputItems.Add(userItem);
            foreach (var output in inputItems)
                followUpOptions.InputItems.Add(output);

            var nextResponse = await responseClient.CreateResponseAsync(followUpOptions, ct);
            currentResponse = nextResponse.Value;
            RecordTokenUsage(currentResponse);
        }

        // ─────────────────────────────────────────────────────────────
        // Step 4: Persist turn to Cosmos
        // ─────────────────────────────────────────────────────────────
        var outputText = currentResponse.GetOutputText();

        await SaveTurnAsync(new ConversationTurn
        {
            ConversationId = conversationId,
            ContextId = request.ContextId ?? "default",
            UserId = request.UserId,
            TurnNumber = priorTurns.Count + 1,
            UserMessage = request.Message,
            AgentResponse = outputText,
            ExtractedKnowledgeIds = extractedKnowledgeIds.Count > 0 ? extractedKnowledgeIds : null,
        });

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

    // ─── Cosmos-backed history ─────────────────────────────────────

    private async Task<List<ConversationTurn>> LoadTurnsAsync(string conversationId)
    {
        if (_turnsContainer is null) return [];

        try
        {
            var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.conversationId = @convId ORDER BY c.turnNumber ASC")
                .WithParameter("@convId", conversationId);

            var turns = new List<ConversationTurn>();
            using var it = _turnsContainer.GetItemQueryIterator<ConversationTurn>(query);
            while (it.HasMoreResults) turns.AddRange(await it.ReadNextAsync());
            return turns;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load turns for {ConversationId} — starting fresh", conversationId);
            return [];
        }
    }

    private async Task SaveTurnAsync(ConversationTurn turn)
    {
        if (_turnsContainer is null) return;

        try
        {
            await _turnsContainer.UpsertItemAsync(turn, new PartitionKey(turn.ConversationId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save turn for {ConversationId}", turn.ConversationId);
        }
    }

    private static string BuildContextSystemMessage(
        DiscoveryContext context, List<ParsedQuestionnaire>? questionnaires = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[DISCOVERY SESSION CONFIGURATION]");
        sb.AppendLine($"Project: {context.Name}");
        sb.AppendLine($"Description: {context.Description}");
        sb.AppendLine($"Mode: {context.DiscoveryMode}");
        sb.AppendLine($"Focus Areas: {string.Join(", ", context.DiscoveryAreas)}");
        sb.AppendLine();

        if (context.KeyQuestions.Count > 0)
        {
            sb.AppendLine("[KEY QUESTIONS]");
            foreach (var q in context.KeyQuestions)
                sb.AppendLine($"- {q}");
            sb.AppendLine();
        }

        if (context.SensitiveAreas.Count > 0)
        {
            sb.AppendLine("[SENSITIVE AREAS — handle with care]");
            foreach (var a in context.SensitiveAreas)
                sb.AppendLine($"- {a}");
            sb.AppendLine();
        }

        if (context.SuccessCriteria.Count > 0)
        {
            sb.AppendLine("[SUCCESS CRITERIA]");
            foreach (var c in context.SuccessCriteria)
                sb.AppendLine($"- {c}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(context.AgentInstructions))
        {
            sb.AppendLine("[AGENT INSTRUCTIONS]");
            sb.AppendLine(context.AgentInstructions);
            sb.AppendLine();
        }

        // ── Inline linked questionnaires ────────────────────────────
        if (questionnaires is { Count: > 0 })
        {
            foreach (var questionnaire in questionnaires)
            {
                sb.AppendLine($"[QUESTIONNAIRE: {questionnaire.Title}]");
                sb.AppendLine($"Description: {questionnaire.Description}");
                sb.AppendLine();

                var orderedSections = questionnaire.Sections
                    .OrderBy(s => s.Order).ToList();

                foreach (var section in orderedSections)
                {
                    sb.AppendLine($"## Section: {section.Title}");
                    if (!string.IsNullOrEmpty(section.Description))
                        sb.AppendLine($"   {section.Description}");

                    var sectionQuestions = questionnaire.Questions
                        .Where(q => q.SectionId == section.SectionId)
                        .OrderBy(q => q.Order)
                        .ToList();

                    foreach (var question in sectionQuestions)
                    {
                        sb.AppendLine($"  [{question.QuestionId}] ({question.QuestionType}) {question.Text}");

                        if (question.Options.Count > 0)
                            sb.AppendLine($"    Options: {string.Join(" | ", question.Options)}");

                        if (question.FollowUpLogic.Count > 0)
                        {
                            foreach (var (trigger, target) in question.FollowUpLogic)
                                sb.AppendLine($"    If '{trigger}' → follow up with: {target}");
                        }
                    }
                    sb.AppendLine();
                }

                sb.AppendLine("Guide the conversation through these sections in order. Do NOT read");
                sb.AppendLine("questions verbatim — ask them conversationally and naturally. Use the");
                sb.AppendLine("question IDs as tags when calling extract_knowledge so scores can be");
                sb.AppendLine("aggregated per question later. Call complete_questionnaire_section when");
                sb.AppendLine("each section is covered.");
                sb.AppendLine();
            }
        }

        sb.AppendLine("Begin the discovery session following the instructions above.");
        return sb.ToString();
    }

    private record ToolCallResult(string Output, List<string>? KnowledgeIds);
    private record KnowledgeExtractionArgs(List<KnowledgeItemArg>? Items);
    private record KnowledgeItemArg(string Content, string Category, double Confidence, List<string>? Tags);
    private record ProfileUpdateArgs(string RoleName, string? Tone, string? DetailLevel, List<string>? PriorityTopics, string? QuestionComplexity);
}
