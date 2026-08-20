namespace KotodamanWordFinder.Models;

public sealed class SearchResult
{
    public string Word { get; init; } = string.Empty;
    public IReadOnlyList<string> Cells { get; init; } = Array.Empty<string>();

    // 0부터 시작하는 판면 위치
    public int StartIndex { get; init; }
    public int EndIndex { get; init; }

    public IReadOnlyList<CharacterAssignment> Assignments { get; init; }
        = Array.Empty<CharacterAssignment>();

    // 추천 문자를 배치한 뒤의 7칸 전체 미리보기입니다.
    public IReadOnlyList<string?> CompletedBoard { get; init; }
        = Array.Empty<string?>();

    // 추천 단어를 배치한 뒤, 새로 배치한 캐릭터 문자를 하나 이상 포함해 성립하는 2~7글자 단어입니다.
    public IReadOnlyList<ComboMatch> ComboMatches { get; init; }
        = Array.Empty<ComboMatch>();

    public int ComboCount => ComboMatches.Count;

    // 첫 턴에는 리더가 1번 손패에 고정되고, 나머지 11명 중 6명을 추가로 확인합니다.
    // 전체 12인 덱 기준 조합 수는 11C6 = 462입니다.
    public int FirstTurnSuccessCount { get; set; }
    public int FirstTurnCombinationCount { get; set; }

    public double FirstTurnSuccessRate =>
        FirstTurnCombinationCount <= 0
            ? 0
            : (double)FirstTurnSuccessCount / FirstTurnCombinationCount;

    public double PracticalScore => ComboCount * FirstTurnSuccessRate;
}

public sealed class ComboMatch
{
    public string Word { get; init; } = string.Empty;
    public int StartIndex { get; init; }
    public int EndIndex { get; init; }
    public int WordLength { get; init; }
}

public sealed class CharacterAssignment
{
    public int BoardIndex { get; init; }
    public string Letter { get; init; } = string.Empty;
    public string CharacterId { get; init; } = string.Empty;
    public string CharacterName { get; init; } = string.Empty;
    public string CharacterFormId { get; init; } = CharacterEntry.BaseFormId;
    public string CharacterFormName { get; init; } = "기본 형태";
    public bool UsesAlternateForm { get; init; }
    public string LetterStateId { get; init; } = CharacterEntry.BaseLetterStateId;
    public string LetterStateName { get; init; } = "기본";
    public string LetterStateKind { get; init; } = string.Empty;
    public string LetterStateNote { get; init; } = string.Empty;
    public bool UsesSpecialLetterState { get; init; }
    public string CharacterGroupName { get; init; } = string.Empty;
    public bool UsesMiracleLeaderLetter { get; init; }
    public string MiracleLeaderName { get; init; } = string.Empty;
    public string MiracleEffectNote { get; init; } = string.Empty;
    public bool UsesDeckGroupConditionLetter { get; init; }
    public string DeckGroupConditionText { get; init; } = string.Empty;
    public string DeckGroupEffectNote { get; init; } = string.Empty;

    // 손패를 선택하지 않았을 때 판면만으로 계산한 일반 추천 문자입니다.
    public bool IsGeneralSuggestion { get; init; }
}

public sealed class SearchGroup
{
    public int WordLength { get; init; }
    public IReadOnlyList<SearchResult> Results { get; init; }
        = Array.Empty<SearchResult>();

    public bool HasResults => Results.Count > 0;
}
