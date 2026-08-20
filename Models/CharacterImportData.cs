namespace KotodamanWordFinder.Models;

public sealed class CharacterImportData
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = CharacterCategories.Other;
    public string Rarity { get; set; } = string.Empty;
    public string Attribute { get; set; } = string.Empty;
    public List<string> SubAttributes { get; set; } = new();
    public string Species { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public List<string> IncludedGroups { get; set; } = new();
    public List<string> GroupCandidates { get; set; } = new();
    public List<string> Letters { get; set; } = new();
    public string ImageUrl { get; set; } = string.Empty;
    public string DownloadedImagePath { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceSite { get; set; } = string.Empty;
    public string MatchedDatabaseUrl { get; set; } = string.Empty;
    public bool? IsCollaboration { get; set; }
    public List<string> Notes { get; set; } = new();
}
