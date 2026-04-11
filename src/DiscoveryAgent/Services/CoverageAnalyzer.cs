using DiscoveryAgent.Core.Interfaces;
using DiscoveryAgent.Core.Models;
using Microsoft.Extensions.Logging;

namespace DiscoveryAgent.Services;

public record CoverageResult
{
    public List<AreaCoverage> Areas { get; init; } = [];
    public int CoveredCount { get; init; }
    public int TotalAreas { get; init; }
    public int CoveragePercent { get; init; }
    public int TotalKnowledgeItems { get; init; }
}

public record AreaCoverage
{
    public string Area { get; init; } = "";
    public bool Covered { get; init; }
    public double MatchScore { get; init; }
    public int ItemCount { get; init; }
}

/// <summary>
/// Analyzes discovery area coverage by matching knowledge item content and tags
/// against the context's discovery areas. Does not rely on question IDs — uses
/// keyword matching to determine which topics have been addressed.
/// </summary>
public class CoverageAnalyzer
{
    private readonly IKnowledgeStore _knowledgeStore;
    private readonly IContextManagementService _contextService;
    private readonly ILogger<CoverageAnalyzer> _logger;

    public CoverageAnalyzer(
        IKnowledgeStore knowledgeStore,
        IContextManagementService contextService,
        ILogger<CoverageAnalyzer> logger)
    {
        _knowledgeStore = knowledgeStore;
        _contextService = contextService;
        _logger = logger;
    }

    /// <summary>
    /// Analyzes coverage for a context by matching knowledge items against discovery areas.
    /// </summary>
    public async Task<CoverageResult> AnalyzeAsync(string contextId)
    {
        var context = await _contextService.GetContextAsync(contextId);
        if (context is null)
        {
            return new CoverageResult();
        }

        var items = await _knowledgeStore.GetByContextAsync(contextId);
        return Analyze(context.DiscoveryAreas, items);
    }

    /// <summary>
    /// Analyzes coverage from pre-fetched data.
    /// </summary>
    public CoverageResult Analyze(List<string> discoveryAreas, List<KnowledgeItem> knowledgeItems)
    {
        var allTags = knowledgeItems
            .SelectMany(i => i.Tags ?? [])
            .Select(t => t.ToLowerInvariant())
            .ToHashSet();

        var allContent = string.Join(" ", knowledgeItems.Select(i => i.Content.ToLowerInvariant()));

        var areas = discoveryAreas.Select(area =>
        {
            var areaLower = area.ToLowerInvariant();
            var keywords = areaLower
                .Split(new[] { ' ', ',', '(', ')', '-', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Distinct()
                .ToList();

            if (keywords.Count == 0)
                return new AreaCoverage { Area = area, Covered = false, MatchScore = 0, ItemCount = 0 };

            var matchCount = keywords.Count(w =>
                allTags.Any(t => t.Contains(w)) || allContent.Contains(w));

            var matchScore = (double)matchCount / keywords.Count;
            var covered = matchScore >= 0.4; // 40% keyword match threshold

            // Count items that specifically mention this area
            var itemCount = knowledgeItems.Count(i =>
            {
                var content = i.Content.ToLowerInvariant();
                var tags = (i.Tags ?? []).Select(t => t.ToLowerInvariant()).ToList();
                return keywords.Any(w => content.Contains(w) || tags.Any(t => t.Contains(w)));
            });

            return new AreaCoverage
            {
                Area = area,
                Covered = covered,
                MatchScore = Math.Round(matchScore, 2),
                ItemCount = itemCount,
            };
        }).ToList();

        var coveredCount = areas.Count(a => a.Covered);
        var totalAreas = areas.Count;
        var pct = totalAreas > 0 ? (int)Math.Round(100.0 * coveredCount / totalAreas) : 0;

        _logger.LogInformation(
            "Coverage analysis: {Covered}/{Total} areas ({Pct}%), {Items} knowledge items",
            coveredCount, totalAreas, pct, knowledgeItems.Count);

        return new CoverageResult
        {
            Areas = areas,
            CoveredCount = coveredCount,
            TotalAreas = totalAreas,
            CoveragePercent = pct,
            TotalKnowledgeItems = knowledgeItems.Count,
        };
    }
}
