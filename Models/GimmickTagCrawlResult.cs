namespace KotodamanWordFinder.Models;

// CharacterWebImportService.CrawlGimmickTagsAsync()의 결과입니다.
public sealed class GimmickTagCrawlResult
{
    public int TotalRows { get; set; }
    public int MatchedCount { get; set; }
    public int FuzzyMatchedCount { get; set; }
    public List<string> UnmatchedNames { get; } = new();
    public Dictionary<string, (List<string> Gimmicks, List<string> Statuses)> TagsByCharacterId { get; } = new();
}
