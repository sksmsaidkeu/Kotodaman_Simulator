namespace KotodamanWordFinder.Models;

public sealed class GaccagUpdateMetadata
{
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public int WordCount { get; set; }
    public int ShortWordCount { get; set; }
    public int SearchWordCount { get; set; }
    public int MinimumLength { get; set; } = 2;
    public int MaximumLength { get; set; } = 7;
    public int QueryCount { get; set; }
    public string LastMode { get; set; } = string.Empty;
    public bool IsPartial { get; set; }
}
