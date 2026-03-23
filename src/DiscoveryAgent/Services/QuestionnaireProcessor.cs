using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Services;

/// <summary>
/// Stub for WP-4. Upload + parse questionnaires via Document Intelligence.
/// </summary>
public class QuestionnaireProcessor : IQuestionnaireProcessor
{
    private readonly ILogger<QuestionnaireProcessor> _logger;
    public QuestionnaireProcessor(ILogger<QuestionnaireProcessor> logger) { _logger = logger; }

    public Task<ParsedQuestionnaire> ParseAsync(Stream documentStream, string fileName)
        => throw new NotImplementedException("WP-4: Document Intelligence parsing");

    public Task<ParsedQuestionnaire?> GetAsync(string questionnaireId)
        => throw new NotImplementedException("WP-4: Retrieve parsed questionnaire");

    public Task AssignToContextAsync(string questionnaireId, string contextId)
        => throw new NotImplementedException("WP-4: Link questionnaire to context");
}
