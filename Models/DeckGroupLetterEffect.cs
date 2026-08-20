namespace KotodamanWordFinder.Models;

public sealed class DeckGroupLetterEffect
{
    // 덱에 아래 대상 그룹 캐릭터가 MinimumCount명 이상 편성되어 있을 때
    // 이 효과를 가진 캐릭터 자신에게 GrantedLetters를 추가합니다.
    public bool IsEnabled { get; set; }

    // 조건 판정에 사용할 그룹. 여러 개를 넣으면 어느 하나에 해당하는 캐릭터를 합산합니다.
    public List<string> TargetGroups { get; set; } = new();

    public int MinimumCount { get; set; } = 2;

    // 조건 달성 시 이 캐릭터 자신에게 추가되는 문자입니다.
    public List<string> GrantedLetters { get; set; } = new();

    public string Note { get; set; } = string.Empty;

    public bool IsConfigured
        => IsEnabled &&
           TargetGroups is { Count: > 0 } &&
           MinimumCount > 0 &&
           GrantedLetters is { Count: > 0 };
}
