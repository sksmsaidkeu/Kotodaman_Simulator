namespace KotodamanWordFinder.Models;

public sealed class GaccagImportProgress
{
    public string Message { get; init; } = string.Empty;
    public int CompletedQueries { get; init; }
    public int? EstimatedQueries { get; init; }
    public int CollectedWords { get; init; }
    public int AddedWords { get; init; }
}
