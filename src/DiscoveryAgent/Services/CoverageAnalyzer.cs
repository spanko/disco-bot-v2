using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Services;

public record CoverageResult
{
    public List<string> CoveredIds { get; init; } = [];
    public List<string> MissedIds { get; init; } = [];
    public Dictionary<string, CoverageSectionSummary> SectionSummaries { get; init; } = new();
    public Dictionary<string, KnowledgeScore> Scores { get; init; } = new();
    public int CoveragePercent { get; init; }
}

public record CoverageSectionSummary
{
    public int Covered { get; init; }
    public int Total { get; init; }
    public int Percent { get; init; }
}

public record KnowledgeScore
{
    public string? Maturity { get; init; }
    public string? Evidence { get; init; }
}

/// <summary>
/// Analyzes question coverage for a conversation by examining knowledge items
/// and their tags for question IDs.
/// </summary>
public class CoverageAnalyzer
{
    private readonly IKnowledgeStore _knowledgeStore;
    private readonly ILogger<CoverageAnalyzer> _logger;

    // Well-known question IDs and their section mappings
    private static readonly string[] AllQuestionIds =
        ["sa-01","sa-02","sa-03","sa-04","eo-01","eo-02","eo-03","eo-04",
         "cp-01","cp-02","cp-03","cp-04","cp-05","cp-06","cm-01","cm-02"];

    private static readonly Dictionary<string, string[]> Sections = new()
    {
        ["strategy-alignment"] = ["sa-01", "sa-02", "sa-03", "sa-04"],
        ["execution-operations"] = ["eo-01", "eo-02", "eo-03", "eo-04"],
        ["culture-people"] = ["cp-01", "cp-02", "cp-03", "cp-04", "cp-05", "cp-06"],
        ["change-management"] = ["cm-01", "cm-02"],
    };

    public CoverageAnalyzer(
        IKnowledgeStore knowledgeStore,
        ILogger<CoverageAnalyzer> logger)
    {
        _knowledgeStore = knowledgeStore;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes coverage for a given context by examining knowledge item tags.
    /// Knowledge items are tagged with question IDs (e.g., "sa-01") and maturity
    /// values (e.g., "maturity:yes") by the agent during extraction.
    /// </summary>
    public async Task<CoverageResult> AnalyzeAsync(string contextId)
    {
        var knowledgeItems = await _knowledgeStore.GetByContextAsync(contextId);
        return Analyze(knowledgeItems);
    }

    /// <summary>
    /// Analyzes coverage from a pre-fetched list of knowledge items.
    /// </summary>
    public CoverageResult Analyze(List<KnowledgeItem> knowledgeItems)
    {
        var coveredIds = new HashSet<string>();
        var scores = new Dictionary<string, KnowledgeScore>();

        foreach (var item in knowledgeItems)
        {
            if (item.Tags is null) continue;

            string? maturity = null;
            var questionIds = new List<string>();

            foreach (var tag in item.Tags)
            {
                if (tag.StartsWith("maturity:"))
                {
                    maturity = tag["maturity:".Length..];
                }
                else if (AllQuestionIds.Contains(tag))
                {
                    questionIds.Add(tag);
                    coveredIds.Add(tag);
                }
            }

            foreach (var qId in questionIds)
            {
                // Store the first (or best) score per question
                if (!scores.ContainsKey(qId))
                {
                    scores[qId] = new KnowledgeScore
                    {
                        Maturity = maturity,
                        Evidence = item.Content.Length > 200
                            ? item.Content[..200] + "..."
                            : item.Content,
                    };
                }
            }
        }

        var missedIds = AllQuestionIds.Where(q => !coveredIds.Contains(q)).ToList();
        var sectionSummaries = new Dictionary<string, CoverageSectionSummary>();

        foreach (var (sectionId, questionIds) in Sections)
        {
            var covered = questionIds.Count(q => coveredIds.Contains(q));
            sectionSummaries[sectionId] = new CoverageSectionSummary
            {
                Covered = covered,
                Total = questionIds.Length,
                Percent = questionIds.Length > 0
                    ? (int)Math.Round(100.0 * covered / questionIds.Length)
                    : 0,
            };
        }

        var totalCoverage = AllQuestionIds.Length > 0
            ? (int)Math.Round(100.0 * coveredIds.Count / AllQuestionIds.Length)
            : 0;

        _logger.LogInformation(
            "Coverage analysis: {Covered}/{Total} ({Pct}%)",
            coveredIds.Count, AllQuestionIds.Length, totalCoverage);

        return new CoverageResult
        {
            CoveredIds = coveredIds.ToList(),
            MissedIds = missedIds,
            SectionSummaries = sectionSummaries,
            Scores = scores,
            CoveragePercent = totalCoverage,
        };
    }
}
