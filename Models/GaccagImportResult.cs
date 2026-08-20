namespace KotodamanWordFinder.Models;

public sealed class GaccagImportResult
{
    public int WordCount { get; init; }
    public int ShortWordCount { get; init; }
    public int SearchWordCount { get; init; }
    public int AddedWordCount { get; init; }
    public int QueryCount { get; init; }
    public bool IsPartial { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
}
