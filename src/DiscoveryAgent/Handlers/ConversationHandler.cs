using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Storage.Blobs;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using DiscoveryAgent.Services;
using DiscoveryAgent.Telemetry;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;
using System.Diagnostics;
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
    private readonly IQuestionnaireProcessor _questionnaireProcessor;
    private readonly BlobServiceClient _blobService;
    private readonly Database _cosmosDb;
    private readonly DiscoveryBotSettings _settings;
    private readonly ILogger<ConversationHandler> _logger;

    public ConversationHandler(
        AIProjectClient projectClient,
        IAgentManager agentManager,
        IKnowledgeStore knowledgeStore,
        IUserProfileService userProfiles,
        IContextManagementService contextService,
        IQuestionnaireProcessor questionnaireProcessor,
        BlobServiceClient blobService,
        Database cosmosDb,
        DiscoveryBotSettings settings,
        ILogger<ConversationHandler> logger)
    {
        _projectClient = projectClient;
        _agentManager = agentManager;
        _knowledgeStore = knowledgeStore;
        _userProfiles = userProfiles;
        _contextService = contextService;
        _questionnaireProcessor = questionnaireProcessor;
        _blobService = blobService;
        _cosmosDb = cosmosDb;
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
            DiscoveryMetrics.ConversationsResumed.Add(1);
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
                    // Fetch any linked questionnaires
                    var questionnaires = new List<ParsedQuestionnaire>();
                    foreach (var qId in context.QuestionnaireIds)
                    {
                        var q = await _questionnaireProcessor.GetAsync(qId);
                        if (q is not null) questionnaires.Add(q);
                        else _logger.LogWarning("Linked questionnaire not found: {QuestionnaireId}", qId);
                    }

                    var contextMessage = BuildContextSystemMessage(context, questionnaires);
                    creationOptions.Items.Add(
                        ResponseItem.CreateSystemMessageItem(contextMessage));
                }
            }

            var conversation = await _projectClient.OpenAI.Conversations
                .CreateProjectConversationAsync(creationOptions, ct);
            conversationId = conversation.Value.Id;

            DiscoveryMetrics.ConversationsCreated.Add(1,
                new KeyValuePair<string, object?>("contextId", request.ContextId ?? "default"));
            _logger.LogInformation("Created conversation: {ConversationId} for context: {ContextId}",
                conversationId, request.ContextId ?? "default");
        }

        // ─────────────────────────────────────────────────────────────
        // Step 2: Generate response via Responses API
        // ─────────────────────────────────────────────────────────────
        // Use a single conversation-bound client for ALL calls (initial
        // + tool follow-ups). This ensures every response is persisted
        // to the conversation in Cosmos, so subsequent turns see resolved
        // function calls. Do NOT use previousResponseId — it conflicts
        // with conversation binding.
        var responseClient = _projectClient.OpenAI.GetProjectResponsesClientForAgent(
            defaultAgent: _agentManager.AgentName,
            defaultConversationId: conversationId);

        // ─────────────────────────────────────────────────────────────
        // Step 2a: Build input message (with optional document content)
        // ─────────────────────────────────────────────────────────────
        var userMessage = request.Message;
        if (request.DocumentIds is { Count: > 0 })
        {
            var docContext = await BuildDocumentContext(request.DocumentIds, ct);
            if (!string.IsNullOrEmpty(docContext))
                userMessage = $"{request.Message}\n\n[ATTACHED DOCUMENTS]\n{docContext}";
        }

        _logger.LogInformation(
            "Creating response: Agent={Agent}, Conversation={ConversationId}, Input={InputLen} chars",
            _agentManager.AgentName, conversationId, userMessage.Length);

        var responseOptions = new CreateResponseOptions();
        responseOptions.InputItems.Add(ResponseItem.CreateUserMessageItem(userMessage));

        var response = await responseClient.CreateResponseAsync(
            responseOptions, ct);
        RecordTokenUsage(response.Value);

        // ─────────────────────────────────────────────────────────────
        // Step 3: Process function tool calls in a loop
        // ─────────────────────────────────────────────────────────────
        // The Responses API returns function_call items when the agent
        // wants to invoke a tool. We execute locally, submit results as
        // input items, and re-call on the same conversation-bound client
        // until no more function calls remain.
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

            // Submit tool results on the SAME conversation-bound client.
            // Conversation binding provides continuity — no previousResponseId needed.
            try
            {
                var followUpOptions = new CreateResponseOptions();
                foreach (var output in inputItems)
                    followUpOptions.InputItems.Add(output);

                var nextResponse = await responseClient.CreateResponseAsync(
                    followUpOptions, ct);

                currentResponse = nextResponse.Value;
                RecordTokenUsage(currentResponse);

                _logger.LogInformation(
                    "Follow-up response: {ResponseId}, Output={OutputLen} chars",
                    currentResponse.Id, currentResponse.GetOutputText().Length);
            }
            catch (Exception ex) when (ex.Message.Contains("No tool output found") || ex.Message.Contains("invalid_request_error"))
            {
                // Conversation state mismatch — retry with only the tool outputs (no echo of function call items)
                _logger.LogWarning(ex, "Tool output submission failed — retrying with outputs only");
                try
                {
                    var outputsOnly = new CreateResponseOptions();
                    foreach (var item in inputItems)
                    {
                        if (item is not FunctionCallResponseItem)
                            outputsOnly.InputItems.Add(item);
                    }
                    if (outputsOnly.InputItems.Count > 0)
                    {
                        var retryResponse = await responseClient.CreateResponseAsync(outputsOnly, ct);
                        currentResponse = retryResponse.Value;
                        RecordTokenUsage(currentResponse);
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception retryEx)
                {
                    _logger.LogWarning(retryEx, "Retry also failed — returning partial response");
                    break;
                }
            }
        }

        var outputText = currentResponse.GetOutputText();

        // Persist conversation turn to Cosmos
        try
        {
            var turnsContainer = _cosmosDb.GetContainer("conversation-turns");
            var turn = new ConversationTurn
            {
                ConversationId = conversationId,
                ContextId = request.ContextId ?? "default",
                UserId = request.UserId,
                UserMessage = request.Message,
                AgentResponse = outputText,
                ExtractedKnowledgeIds = extractedKnowledgeIds.Count > 0 ? extractedKnowledgeIds : null,
            };
            await turnsContainer.UpsertItemAsync(turn, new PartitionKey(conversationId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist conversation turn for {ConversationId}", conversationId);
        }

        return new ConversationResponse(
            ConversationId: conversationId,
            Response: outputText,
            AgentName: _agentManager.AgentName,
            ExtractedKnowledgeIds: extractedKnowledgeIds.Count > 0 ? extractedKnowledgeIds : null
        );
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

    // ─── Token usage tracking ─────────────────────────────────────────

    private void RecordTokenUsage(ResponseResult response)
    {
        DiscoveryMetrics.ResponseCalls.Add(1);
        var usage = response.Usage;
        if (usage is null) return;

        DiscoveryMetrics.InputTokens.Add(usage.InputTokenCount);
        DiscoveryMetrics.OutputTokens.Add(usage.OutputTokenCount);
        DiscoveryMetrics.TotalTokens.Add(usage.TotalTokenCount);

        _logger.LogInformation(
            "Token usage: Input={InputTokens}, Output={OutputTokens}, Total={TotalTokens}",
            usage.InputTokenCount, usage.OutputTokenCount, usage.TotalTokenCount);
    }

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

    // ─── Document handling ──────────────────────────────────────────

    private async Task<string> BuildDocumentContext(List<string> documentIds, CancellationToken ct)
    {
        var container = _blobService.GetBlobContainerClient("documents");
        var parts = new List<string>();

        foreach (var docId in documentIds)
        {
            try
            {
                // Find the blob by scanning for the document ID in the name
                await foreach (var blob in container.GetBlobsAsync(
                    new Azure.Storage.Blobs.Models.GetBlobsOptions { Traits = Azure.Storage.Blobs.Models.BlobTraits.Metadata }, ct))
                {
                    if (!blob.Name.Contains(docId)) continue;

                    var blobClient = container.GetBlobClient(blob.Name);
                    var contentType = blob.Properties.ContentType ?? "";
                    var originalName = blob.Metadata is not null && blob.Metadata.TryGetValue("originalName", out var name) ? name : blob.Name;

                    if (contentType.StartsWith("image/"))
                    {
                        // For images: download and encode as base64 for GPT-4o vision
                        using var ms = new MemoryStream();
                        await blobClient.DownloadToAsync(ms, ct);
                        var base64 = Convert.ToBase64String(ms.ToArray());
                        parts.Add($"--- Image: {originalName} ---\n[Image uploaded as {contentType}, {ms.Length} bytes. Base64 data available for analysis.]\ndata:{contentType};base64,{base64}");
                    }
                    else
                    {
                        // For documents: download text content
                        // Simple text extraction — for PDFs and Word docs,
                        // the content is stored as-is in blob; Document Intelligence
                        // can be added later for richer extraction
                        using var ms = new MemoryStream();
                        await blobClient.DownloadToAsync(ms, ct);
                        ms.Position = 0;

                        var text = await ExtractTextFromDocument(ms, contentType, originalName);
                        if (!string.IsNullOrEmpty(text))
                            parts.Add($"--- Document: {originalName} ---\n{text}");
                        else
                            parts.Add($"--- Document: {originalName} ---\n[Document uploaded ({contentType}, {ms.Length} bytes) but text extraction not available. Please acknowledge receipt.]");
                    }

                    break; // Found the blob, move to next docId
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load document {DocumentId}", docId);
                parts.Add($"[Failed to load document {docId}]");
            }
        }

        return string.Join("\n\n", parts);
    }

    private static async Task<string?> ExtractTextFromDocument(
        MemoryStream stream, string contentType, string fileName)
    {
        // Plain text files
        if (contentType.Contains("text/"))
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }

        // For PDF and Word docs, return a placeholder indicating content is available
        // Full Document Intelligence integration can be added as an enhancement
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        if (ext is ".pdf" or ".docx" or ".doc")
        {
            return $"[{Path.GetExtension(fileName).ToUpperInvariant().TrimStart('.')} document, {stream.Length:N0} bytes. Content extraction pending Document Intelligence integration.]";
        }

        return null;
    }

    // ─── Deserialization helpers ─────────────────────────────────────

    private record ToolCallResult(string Output, List<string>? KnowledgeIds);
    private record KnowledgeExtractionArgs(List<KnowledgeItemArg>? Items);
    private record KnowledgeItemArg(string Content, string Category, double Confidence, List<string>? Tags);
    private record ProfileUpdateArgs(string RoleName, string? Tone, string? DetailLevel, List<string>? PriorityTopics, string? QuestionComplexity);
}
