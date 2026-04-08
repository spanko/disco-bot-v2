using Azure.AI.Projects;
using DiscoveryAgent.Configuration;
using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using System.Text.Json;

namespace DiscoveryAgent.Services;

/// <summary>
/// Request model for a single test run.
/// </summary>
public record TestRunRequest
{
    public string ContextId { get; init; } = "";
    public TestPersona Persona { get; init; } = new();
    public string ResponseMode { get; init; } = "realistic";
    public int MaxTurns { get; init; } = 60;
}

public record TestPersona
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Style { get; init; } = "";
    public string Depth { get; init; } = "";
    public List<string> Traits { get; init; } = [];
}

public record TestTurnEvent
{
    public string Type { get; init; } = "turn";
    public int TurnNumber { get; init; }
    public string Role { get; init; } = "";
    public string Content { get; init; } = "";
    public List<string> ExtractedKnowledgeIds { get; init; } = [];
    public List<string> CoveredQuestionIds { get; init; } = [];
}

public record TestCompleteEvent
{
    public string Type { get; init; } = "complete";
    public int TotalTurns { get; init; }
    public List<string> CoveredQuestionIds { get; init; } = [];
    public List<string> MissedQuestionIds { get; init; } = [];
    public int CoveragePercent { get; init; }
    public string ConversationId { get; init; } = "";
}

/// <summary>
/// Orchestrates LLM-to-LLM conversations for test harness runs.
/// The agent side uses the real IConversationHandler (calling the agent exactly as a real user would).
/// The respondent side uses a plain ChatClient (no agent binding) with a persona system prompt.
/// </summary>
public class TestRunnerService
{
    private readonly AIProjectClient _projectClient;
    private readonly DiscoveryBotSettings _settings;
    private readonly ILogger<TestRunnerService> _logger;

    // Well-known question IDs for coverage tracking
    private static readonly string[] AllQuestionIds =
        ["sa-01","sa-02","sa-03","sa-04","eo-01","eo-02","eo-03","eo-04",
         "cp-01","cp-02","cp-03","cp-04","cp-05","cp-06","cm-01","cm-02"];

    public TestRunnerService(
        AIProjectClient projectClient,
        DiscoveryBotSettings settings,
        ILogger<TestRunnerService> logger)
    {
        _projectClient = projectClient;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Runs a single test conversation, yielding events as they occur.
    /// </summary>
    public async IAsyncEnumerable<object> RunAsync(
        TestRunRequest request,
        IConversationHandler handler,
        IKnowledgeStore knowledgeStore,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var userId = $"test-runner-{timestamp}";
        var coveredIds = new HashSet<string>();
        int turnNumber = 0;

        _logger.LogInformation(
            "Starting test run: Persona={Persona}, Mode={Mode}, MaxTurns={MaxTurns}, Context={Context}",
            request.Persona.Name, request.ResponseMode, request.MaxTurns, request.ContextId);

        // Step 1: Send initial message to create the conversation
        var initialMessage = "Hi, I'm ready to start the assessment.";
        var conversationRequest = new ConversationRequest(
            UserId: userId,
            Message: initialMessage,
            ContextId: request.ContextId);

        var agentResponse = await handler.HandleAsync(conversationRequest, ct);
        var conversationId = agentResponse.ConversationId;
        turnNumber++;

        // Emit agent opening turn
        var agentTurn = new TestTurnEvent
        {
            TurnNumber = turnNumber,
            Role = "agent",
            Content = agentResponse.Response,
            ExtractedKnowledgeIds = agentResponse.ExtractedKnowledgeIds ?? [],
        };
        UpdateCoverage(coveredIds, agentResponse.ExtractedKnowledgeIds, knowledgeStore, request.ContextId);
        agentTurn = agentTurn with { CoveredQuestionIds = coveredIds.ToList() };
        yield return agentTurn;

        // Conversation history for respondent context
        var conversationHistory = new List<(string role, string content)>
        {
            ("user", initialMessage),
            ("agent", agentResponse.Response)
        };

        // Build respondent system prompt
        var respondentPrompt = BuildRespondentPrompt(request.Persona, request.ResponseMode);

        // Get a plain ChatClient (no agent binding) for the respondent
        var chatClient = _projectClient.OpenAI.GetChatClient(_settings.ModelDeploymentName);

        // Step 2: Loop until maxTurns or agent signals completion
        while (turnNumber < request.MaxTurns && !ct.IsCancellationRequested)
        {
            // Pace the conversation to avoid rate limits (429)
            await Task.Delay(2000, ct);

            // Generate respondent reply with retry on 429
            var respondentReply = await WithRetry(
                () => GenerateRespondentReply(chatClient, respondentPrompt, conversationHistory, ct),
                ct);
            turnNumber++;

            var respondentTurn = new TestTurnEvent
            {
                TurnNumber = turnNumber,
                Role = "respondent",
                Content = respondentReply,
                CoveredQuestionIds = coveredIds.ToList(),
            };
            yield return respondentTurn;

            conversationHistory.Add((role: "respondent", content: respondentReply));

            // Check for completion signals
            if (IsConversationComplete(agentResponse.Response))
            {
                _logger.LogInformation("Agent signaled completion at turn {Turn}", turnNumber);
                break;
            }

            // Send respondent reply to agent (with retry on 429)
            var followUpRequest = new ConversationRequest(
                UserId: userId,
                Message: respondentReply,
                ConversationId: conversationId,
                ContextId: request.ContextId);

            try
            {
                agentResponse = await WithRetry(
                    () => handler.HandleAsync(followUpRequest, ct),
                    ct);
            }
            catch (Exception ex) when (ex.Message.Contains("400") || ex.Message.Contains("tool output") || ex.Message.Contains("invalid_request"))
            {
                // Conversation state is corrupted (unresolved tool calls).
                // Start a fresh conversation for remaining turns.
                _logger.LogWarning(ex, "Conversation state corrupted at turn {Turn} — starting fresh conversation", turnNumber);
                var freshRequest = new ConversationRequest(
                    UserId: userId,
                    Message: $"[Continuing from a prior conversation] The respondent said: {respondentReply}",
                    ContextId: request.ContextId);
                agentResponse = await WithRetry(
                    () => handler.HandleAsync(freshRequest, ct),
                    ct);
                conversationId = agentResponse.ConversationId;
            }
            turnNumber++;

            UpdateCoverage(coveredIds, agentResponse.ExtractedKnowledgeIds, knowledgeStore, request.ContextId);

            var nextAgentTurn = new TestTurnEvent
            {
                TurnNumber = turnNumber,
                Role = "agent",
                Content = agentResponse.Response,
                ExtractedKnowledgeIds = agentResponse.ExtractedKnowledgeIds ?? [],
                CoveredQuestionIds = coveredIds.ToList(),
            };
            yield return nextAgentTurn;

            conversationHistory.Add(("agent", agentResponse.Response));

            // Check again after agent response
            if (IsConversationComplete(agentResponse.Response))
            {
                _logger.LogInformation("Agent signaled completion at turn {Turn}", turnNumber);
                break;
            }
        }

        // Emit completion event
        var missedIds = AllQuestionIds.Where(q => !coveredIds.Contains(q)).ToList();
        var coveragePct = AllQuestionIds.Length > 0
            ? (int)Math.Round(100.0 * coveredIds.Count / AllQuestionIds.Length)
            : 0;

        yield return new TestCompleteEvent
        {
            TotalTurns = turnNumber,
            CoveredQuestionIds = coveredIds.ToList(),
            MissedQuestionIds = missedIds,
            CoveragePercent = coveragePct,
            ConversationId = conversationId,
        };

        _logger.LogInformation(
            "Test run complete: {TotalTurns} turns, {Coverage}% coverage, ConversationId={ConversationId}",
            turnNumber, coveragePct, conversationId);
    }

    private static string BuildRespondentPrompt(TestPersona persona, string responseMode)
    {
        var modeDescription = responseMode switch
        {
            "adversarial" => "Give short answers, push back, say 'N/A' or 'I don't know' frequently, and challenge the agent's questions.",
            "golden" => "Give thorough, cooperative answers with specific examples. Aim to provide the agent with everything it needs.",
            "random" => "Vary your cooperation, length, and specificity randomly across responses. Some answers are short and dismissive, others are detailed and helpful.",
            _ => "Vary your response length naturally. Sometimes give detailed answers, sometimes brief ones. Occasionally say 'I don't know' or go on tangents.",
        };

        return $"""
            You are simulating a survey respondent for a team health assessment.

            **Your persona:** {persona.Name}
            - Communication style: {persona.Style}
            - Depth of answers: {persona.Depth}
            - Behavioral traits: {string.Join(", ", persona.Traits)}

            **Response mode:** {responseMode}
            - {modeDescription}

            **Rules:**
            - Stay in character for the entire conversation.
            - Never reveal you are an AI or a test respondent.
            - Answer based on a fictional but plausible team scenario.
            - If the agent asks a question you can't answer in character, say so naturally (e.g., "I'm not sure, I'd have to check with my manager").
            - Match your response length to your persona -- executives are brief, ICs give detail, skeptics push back.
            - If in adversarial mode, give short answers, say "N/A" or "I don't know" frequently, and challenge the agent's questions.
            - If in golden path mode, give thorough, cooperative answers with specific examples.
            - If in randomized mode, vary your cooperation, length, and specificity randomly across responses.
            """;
    }

    private static async Task<string> GenerateRespondentReply(
        ChatClient chatClient,
        string systemPrompt,
        List<(string role, string content)> conversationHistory,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        foreach (var (role, content) in conversationHistory)
        {
            if (role == "respondent")
                messages.Add(new AssistantChatMessage(content));
            else
                messages.Add(new UserChatMessage(content));
        }

        var completion = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);
        return completion.Value.Content[0].Text;
    }

    private static void UpdateCoverage(
        HashSet<string> coveredIds,
        List<string>? extractedKnowledgeIds,
        IKnowledgeStore knowledgeStore,
        string contextId)
    {
        // In a real implementation we would query knowledge items by their IDs
        // and check tags for question IDs. For now, we track based on the
        // knowledge extraction happening (the agent tags items with question IDs).
        // The CoverageAnalyzer handles the full query.
        if (extractedKnowledgeIds is not null)
        {
            // We'll let the CoverageAnalyzer do the detailed query;
            // here we just note that extraction occurred.
        }
    }

    private static async Task<T> WithRetry<T>(Func<Task<T>> action, CancellationToken ct, int maxRetries = 3)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < maxRetries && ex.Message.Contains("429"))
            {
                var delay = (attempt + 1) * 10_000; // 10s, 20s, 30s
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsConversationComplete(string agentResponse)
    {
        var lower = agentResponse.ToLowerInvariant();
        return lower.Contains("thank you for completing") ||
               lower.Contains("assessment is complete") ||
               lower.Contains("we've covered all") ||
               lower.Contains("that wraps up") ||
               lower.Contains("all sections complete");
    }
}
