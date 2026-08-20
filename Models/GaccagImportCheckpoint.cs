namespace KotodamanWordFinder.Models;

public sealed class GaccagImportCheckpoint
{
    public string Mode { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public int QueryCount { get; set; }
    public List<string> CompletedQueries { get; set; } = new();
    public List<string> PendingQueries { get; set; } = new();
}
