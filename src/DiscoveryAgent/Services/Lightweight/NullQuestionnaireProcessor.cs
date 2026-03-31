using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;

namespace DiscoveryAgent.Services.Lightweight;

/// <summary>
/// No-op questionnaire processor for lightweight mode.
/// </summary>
public class NullQuestionnaireProcessor : IQuestionnaireProcessor
{
    public Task<ParsedQuestionnaire> ParseAsync(Stream documentStream, string fileName) =>
        throw new NotSupportedException("Questionnaire processing is not available in lightweight mode.");

    public Task<ParsedQuestionnaire?> GetAsync(string questionnaireId) =>
        Task.FromResult<ParsedQuestionnaire?>(null);

    public Task AssignToContextAsync(string questionnaireId, string contextId) =>
        throw new NotSupportedException("Questionnaire processing is not available in lightweight mode.");
}
