using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Services;

/// <summary>
/// Cosmos-backed questionnaire processor. Reads parsed questionnaires from the
/// 'questionnaires' container (uploaded via admin API) and links them to contexts.
/// 
/// Note: ParseAsync (Document Intelligence) is still a future enhancement.
/// The primary path is direct JSON upload via /api/manage/questionnaire.
/// </summary>
public class QuestionnaireProcessor : IQuestionnaireProcessor
{
    private readonly Database _cosmosDb;
    private readonly IContextManagementService _contextService;
    private readonly ILogger<QuestionnaireProcessor> _logger;

    public QuestionnaireProcessor(
        Database cosmosDb,
        IContextManagementService contextService,
        ILogger<QuestionnaireProcessor> logger)
    {
        _cosmosDb = cosmosDb;
        _contextService = contextService;
        _logger = logger;
    }

    public Task<ParsedQuestionnaire> ParseAsync(Stream documentStream, string fileName)
        => throw new NotImplementedException("Document Intelligence parsing — upload JSON via /api/manage/questionnaire instead.");

    public async Task<ParsedQuestionnaire?> GetAsync(string questionnaireId)
    {
        try
        {
            var container = _cosmosDb.GetContainer("questionnaires");
            var response = await container.ReadItemAsync<ParsedQuestionnaire>(
                questionnaireId, new PartitionKey(questionnaireId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Questionnaire not found: {QuestionnaireId}", questionnaireId);
            return null;
        }
    }

    public async Task AssignToContextAsync(string questionnaireId, string contextId)
    {
        var context = await _contextService.GetContextAsync(contextId);
        if (context is null)
            throw new InvalidOperationException($"Context not found: {contextId}");

        if (!context.QuestionnaireIds.Contains(questionnaireId))
        {
            var updated = context with
            {
                QuestionnaireIds = [.. context.QuestionnaireIds, questionnaireId]
            };
            await _contextService.UpsertContextAsync(updated);
            _logger.LogInformation("Assigned questionnaire {QuestionnaireId} to context {ContextId}",
                questionnaireId, contextId);
        }
    }
}
