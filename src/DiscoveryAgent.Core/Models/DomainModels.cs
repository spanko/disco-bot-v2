using System.Text.Json.Serialization;

namespace DiscoveryAgent.Core.Models;

// ============================================================================
// Discovery Context & Configuration
// ============================================================================

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscoveryMode { Exploratory, Structured, Hybrid }

public record DiscoveryContext
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ContextId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public DiscoveryMode DiscoveryMode { get; init; } = DiscoveryMode.Hybrid;
    public List<string> DiscoveryAreas { get; init; } = [];
    public List<string> KeyQuestions { get; init; } = [];
    public List<string> SensitiveAreas { get; init; } = [];
    public List<string> SuccessCriteria { get; init; } = [];
    public string AgentInstructions { get; init; } = "";
    public List<string> QuestionnaireIds { get; init; } = [];
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

// ============================================================================
// User Profiles & Roles
// ============================================================================

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommunicationTone { Formal, Conversational, Technical }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DetailLevel { Executive, Detailed, Technical }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuestionComplexity { HighLevel, Detailed, DeepDive }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResponseFormat { Bullets, Narrative, Structured }

public record UserProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string UserId { get; init; } = "";
    public string RoleName { get; init; } = "";
    public CommunicationTone Tone { get; init; } = CommunicationTone.Conversational;
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Detailed;
    public List<string> PriorityTopics { get; init; } = [];
    public QuestionComplexity QuestionComplexity { get; init; } = QuestionComplexity.Detailed;
    public ResponseFormat ResponseFormat { get; init; } = ResponseFormat.Narrative;
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
}

// ============================================================================
// Knowledge Items with Attribution
// ============================================================================

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeCategory { Fact, Opinion, Decision, Requirement, Concern }

public record KnowledgeItem
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Content { get; init; } = "";
    public KnowledgeCategory Category { get; init; }
    public double Confidence { get; init; }

    // Attribution
    public string SourceUserId { get; init; } = "";
    public string SourceUserRole { get; init; } = "";
    public string SourceThreadId { get; init; } = "";
    public string SourceConversationId { get; init; } = "";  // NEW: Responses API conversation ID
    public string SourceMessageId { get; init; } = "";
    public DateTime ExtractionTimestamp { get; init; } = DateTime.UtcNow;

    // Context links
    public string RelatedContextId { get; init; } = "";
    public string? SectionId { get; init; }
    public string? QuestionId { get; init; }

    // Relationships
    public List<string> RelatedItems { get; init; } = [];
    public string? Supersedes { get; init; }
    public List<string> Tags { get; init; } = [];
    public bool Verified { get; init; }
}

public record AggregatedKnowledge
{
    public string Topic { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<string> SupportingItemIds { get; init; } = [];
    public string ConsensusLevel { get; init; } = "";
    public int SourceCount { get; init; }
    public DateTime LastUpdated { get; init; } = DateTime.UtcNow;
}

public record KnowledgeProvenance(
    KnowledgeItem Item,
    string SourceUserId,
    string SourceUserRole,
    string ConversationId,
    DateTime ExtractionTimestamp,
    bool Verified
);

// ============================================================================
// Questionnaire Models
// ============================================================================

public record QuestionnaireSection
{
    public string SectionId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ParentSectionId { get; init; }
    public int Order { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QuestionType { Open, MultipleChoice, Scale, YesNo }

public record Question
{
    public string QuestionId { get; init; } = "";
    public string SectionId { get; init; } = "";
    public string Text { get; init; } = "";
    public QuestionType QuestionType { get; init; } = QuestionType.Open;
    public List<string> Options { get; init; } = [];
    public Dictionary<string, string> FollowUpLogic { get; init; } = new();
    public bool Required { get; init; } = true;
    public int Order { get; init; }
}

public record ParsedQuestionnaire
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string QuestionnaireId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public List<QuestionnaireSection> Sections { get; init; } = [];
    public List<Question> Questions { get; init; } = [];
    public Dictionary<string, object> Metadata { get; init; } = new();
    public DateTime UploadedAt { get; init; } = DateTime.UtcNow;
}

// ============================================================================
// Discovery Session Tracking
// ============================================================================

public record DiscoverySession
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string ContextId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string ConversationId { get; init; } = "";  // NEW: Responses API conversation
    public string Status { get; init; } = "active";
    public List<string> CompletedSections { get; init; } = [];
    public List<string> ExtractedKnowledgeIds { get; init; } = [];
    public List<string> UploadedDocumentIds { get; init; } = [];
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; init; }
}

// ============================================================================
// API Request/Response Models
// ============================================================================

public record ConversationRequest(
    string UserId,
    string Message,
    string? ConversationId = null,
    string? ContextId = null,
    List<string>? DocumentIds = null
);

public record ConversationResponse(
    string ConversationId,
    string Response,
    string AgentName,
    List<string>? ExtractedKnowledgeIds = null
);

public record DiscoveryContextConfig(
    string ContextId,
    string Name,
    string Description,
    string DiscoveryMode,
    List<string> DiscoveryAreas,
    List<string> KeyQuestions,
    List<string> SensitiveAreas,
    List<string> SuccessCriteria,
    string AgentInstructions
);
