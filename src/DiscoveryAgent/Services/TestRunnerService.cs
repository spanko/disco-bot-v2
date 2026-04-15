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
    public int CoveragePercent { get; init; }
    public int CoveredAreas { get; init; }
    public int TotalAreas { get; init; }
}

public record TestCompleteEvent
{
    public string Type { get; init; } = "complete";
    public int TotalTurns { get; init; }
    public int CoveragePercent { get; init; }
    public int CoveredAreas { get; init; }
    public int TotalAreas { get; init; }
    public List<AreaCoverage>? Areas { get; init; }
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
    private readonly CoverageAnalyzer _coverageAnalyzer;
    private readonly DiscoveryBotSettings _settings;
    private readonly ILogger<TestRunnerService> _logger;

    public TestRunnerService(
        AIProjectClient projectClient,
        CoverageAnalyzer coverageAnalyzer,
        DiscoveryBotSettings settings,
        ILogger<TestRunnerService> logger)
    {
        _projectClient = projectClient;
        _coverageAnalyzer = coverageAnalyzer;
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
        var coverage = await _coverageAnalyzer.AnalyzeAsync(request.ContextId);
        agentTurn = agentTurn with { CoveragePercent = coverage.CoveragePercent, CoveredAreas = coverage.CoveredCount, TotalAreas = coverage.TotalAreas };
        yield return agentTurn;

        // Conversation history for respondent context
        var conversationHistory = new List<(string role, string content)>
        {
            ("user", initialMessage),
            ("agent", agentResponse.Response)
        };

        // Get a plain ChatClient (no agent binding) for the respondent
        var chatClient = _projectClient.OpenAI.GetChatClient(_settings.ModelDeploymentName);

        // Step 2: Loop until maxTurns or agent signals completion
        while (turnNumber < request.MaxTurns && !ct.IsCancellationRequested)
        {
            // Pace the conversation to avoid rate limits (429)
            await Task.Delay(3000, ct);

            // Rebuild respondent prompt with current coverage so persona steers toward uncovered areas
            var respondentPrompt = BuildRespondentPrompt(request.Persona, request.ResponseMode, coverage);

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
                CoveragePercent = coverage.CoveragePercent,
                CoveredAreas = coverage.CoveredCount,
                TotalAreas = coverage.TotalAreas,
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

            coverage = await _coverageAnalyzer.AnalyzeAsync(request.ContextId);

            var nextAgentTurn = new TestTurnEvent
            {
                TurnNumber = turnNumber,
                Role = "agent",
                Content = agentResponse.Response,
                ExtractedKnowledgeIds = agentResponse.ExtractedKnowledgeIds ?? [],
                CoveragePercent = coverage.CoveragePercent,
                CoveredAreas = coverage.CoveredCount,
                TotalAreas = coverage.TotalAreas,
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

        // Emit completion event with final coverage
        coverage = await _coverageAnalyzer.AnalyzeAsync(request.ContextId);

        yield return new TestCompleteEvent
        {
            TotalTurns = turnNumber,
            CoveragePercent = coverage.CoveragePercent,
            CoveredAreas = coverage.CoveredCount,
            TotalAreas = coverage.TotalAreas,
            Areas = coverage.Areas,
            ConversationId = conversationId,
        };

        _logger.LogInformation(
            "Test run complete: {TotalTurns} turns, {Coverage}% coverage ({Covered}/{Total} areas), ConversationId={ConversationId}",
            turnNumber, coverage.CoveragePercent, coverage.CoveredCount, coverage.TotalAreas, conversationId);
    }

    private static string BuildRespondentPrompt(TestPersona persona, string responseMode, CoverageResult? coverage = null)
    {
        var modeDescription = responseMode switch
        {
            "adversarial" => "Give short answers, push back, say 'N/A' or 'I don't know' frequently, and challenge the agent's questions.",
            "golden" => "Give thorough, cooperative answers with specific examples. Aim to provide the agent with everything it needs.",
            "random" => "Vary your cooperation, length, and specificity randomly across responses. Some answers are short and dismissive, others are detailed and helpful.",
            _ => "Vary your response length naturally. Sometimes give detailed answers, sometimes brief ones. Occasionally say 'I don't know' or go on tangents.",
        };

        var coverageGuidance = "";
        if (coverage?.Areas is { Count: > 0 })
        {
            var covered = coverage.Areas.Where(a => a.Covered).Select(a => a.Area).ToList();
            var uncovered = coverage.Areas.Where(a => !a.Covered).Select(a => a.Area).ToList();

            if (covered.Count > 0 && uncovered.Count > 0)
            {
                coverageGuidance = $"""

                    **Coverage guidance — IMPORTANT:**
                    The following areas have ALREADY been covered: {string.Join(", ", covered)}.
                    The following areas have NOT been covered yet: {string.Join(", ", uncovered)}.

                    If the agent keeps asking about already-covered topics, actively steer the conversation
                    toward uncovered areas. For example, say things like: "I think we've covered that pretty well.
                    Can we talk about [uncovered topic]?" or "Actually, what I really wanted to discuss is
                    [uncovered topic]." or "That reminds me of something related to [uncovered topic]."

                    Your goal is to help ensure ALL areas get covered, not just the first few.
                    """;
            }
        }

        return $"""
            You are simulating a survey respondent for a team health assessment.

            **Your persona:** {persona.Name}
            - Communication style: {persona.Style}
            - Depth of answers: {persona.Depth}
            - Behavioral traits: {string.Join(", ", persona.Traits)}

            **Response mode:** {responseMode}
            - {modeDescription}
            {coverageGuidance}

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
