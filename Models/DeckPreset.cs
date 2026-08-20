namespace KotodamanWordFinder.Models;

public sealed class DeckPreset
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> CharacterIds { get; set; } = new();
}
