using System.Diagnostics.Metrics;

namespace DiscoveryAgent.Telemetry;

public static class DiscoveryMetrics
{
    public static readonly Meter Meter = new("DiscoveryBot", "1.0.0");

    // Knowledge extraction
    public static readonly Counter<int> KnowledgeItemsExtracted =
        Meter.CreateCounter<int>("discovery.knowledge.items_extracted", "items", "Knowledge items extracted per tool call");
    public static readonly Histogram<double> ExtractionConfidence =
        Meter.CreateHistogram<double>("discovery.knowledge.confidence", "score", "Confidence distribution of extracted items");
    public static readonly Counter<int> ExtractionFailures =
        Meter.CreateCounter<int>("discovery.knowledge.extraction_failures", "failures", "Failed extraction tool calls");

    // Session quality
    public static readonly Histogram<double> SessionDuration =
        Meter.CreateHistogram<double>("discovery.session.duration_seconds", "seconds", "Session duration");
    public static readonly Histogram<int> MessagesPerSession =
        Meter.CreateHistogram<int>("discovery.session.message_count", "messages", "Messages per session");
    public static readonly Counter<int> SectionsCompleted =
        Meter.CreateCounter<int>("discovery.questionnaire.sections_completed", "sections", "Questionnaire sections completed");

    // Tool calls
    public static readonly Counter<int> ToolCallsTotal =
        Meter.CreateCounter<int>("discovery.tools.calls_total", "calls", "Total tool calls by function name");
    public static readonly Histogram<double> ToolCallDuration =
        Meter.CreateHistogram<double>("discovery.tools.duration_ms", "ms", "Tool call execution time");

    // Conversations
    public static readonly Counter<int> ConversationsCreated =
        Meter.CreateCounter<int>("discovery.conversations.created", "conversations", "New conversations started");
    public static readonly Counter<int> ConversationsResumed =
        Meter.CreateCounter<int>("discovery.conversations.resumed", "conversations", "Existing conversations resumed");

    // Errors
    public static readonly Counter<int> AgentErrors =
        Meter.CreateCounter<int>("discovery.errors", "errors", "Agent errors by type");
}
