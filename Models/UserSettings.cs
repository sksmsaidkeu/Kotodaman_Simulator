namespace KotodamanWordFinder.Models;

public sealed class UserSettings
{
    public List<string> SelectedHandCharacterIds { get; set; } = new();

    // 캐릭터 ID -> 현재 손패에서 선택한 문자 상태 ID
    public Dictionary<string, string> SelectedHandLetterStateIds { get; set; }
        = new(StringComparer.Ordinal);

    // 캐릭터 ID -> 현재 손패에서 선택한 동일 이름 모드시프트 형태 ID
    public Dictionary<string, string> SelectedHandFormIds { get; set; }
        = new(StringComparer.Ordinal);

    public List<string?> BoardCells { get; set; } = new();
    public bool AutoSearchEnabled { get; set; } = true;

    // 덱 전체 결과 정렬: Practical / Probability / Combo
    public string DeckResultSortMode { get; set; } = "Practical";

    // 덱 편집 창을 다시 열었을 때 마지막으로 선택했던 캐릭터를 복원합니다.
    public string LastDeckEditorCharacterId { get; set; } = string.Empty;

    // 덱 편집 창의 검색/필터 상태를 그대로 복원합니다.
    public string LastDeckEditorSearchText { get; set; } = string.Empty;
    public string LastDeckEditorGroupFilter { get; set; } = "전체 그룹";
    public string LastDeckEditorCategoryFilter { get; set; } = "전체 등급";
    public string LastDeckEditorAttributeFilter { get; set; } = "전체 속성";
    public string LastDeckEditorSpeciesFilter { get; set; } = "전체 종족";
    public string LastDeckEditorStatusFilter { get; set; } = "전체 상태";
    public string LastDeckEditorSortMode { get; set; } = "기본 정렬";
    public bool LastDeckEditorFavoritesOnly { get; set; }
    public bool LastDeckEditorBelovedOnly { get; set; }
}
