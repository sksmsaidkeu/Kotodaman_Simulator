namespace KotodamanWordFinder.Models;

public sealed class CharacterLetterState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = CharacterLetterStateKinds.Conditional;

    // true: 기본 문자에 아래 문자를 추가, false: 아래 문자로 기본 문자를 대체
    public bool IncludeBaseLetters { get; set; } = true;

    public List<string> Letters { get; set; } = new();
    public string Note { get; set; } = string.Empty;
}
