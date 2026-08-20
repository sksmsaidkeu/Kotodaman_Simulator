using System.Text.Json.Serialization;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder.Models;

public sealed class CharacterEntry
{
    public const string BaseLetterStateId = "__base__";
    public const string BaseFormId = "__base_form__";
    public const string AllFormsId = "__all_forms__";

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // 일본어 한자 이름을 한글 등 다른 표기로 검색하기 위한 사용자 지정 별칭입니다.
    // 예: 竈門炭治郎 -> 카마도 탄지로
    public List<string> SearchAliases { get; set; } = new();

    // 캐릭터 목록을 찾기 쉽게 나누는 분류
    public string Category { get; set; } = CharacterCategories.Other;

    // 게임 내 기본 속성 (火/水/木/光/闇/天/冥/虹). 비어 있으면 미입력입니다.
    public string Attribute { get; set; } = string.Empty;

    // 서브/변환 가능한 속성. 메인 속성과 별도로 여러 개를 가질 수 있습니다.
    public List<string> SubAttributes { get; set; } = new();

    // 게임 내 종족 (神/魔/英/龍/獣/霊/物/妖). 비어 있으면 미입력입니다.
    public string Species { get; set; } = string.Empty;

    // 캐릭터는 기본적으로 하나의 소속 그룹만 가집니다.
    public string GroupName { get; set; } = string.Empty;

    // 상위 그룹이 다른 그룹으로도 취급되는 경우의 포함 규칙입니다.
    // 예: 全の戦律 캐릭터는 斬・砲・突・重・超・打の戦律 대상으로도 취급됩니다.
    public List<string> IncludedGroups { get; set; } = new();

    // 자주 쓰는 캐릭터를 목록 상단에 고정
    public bool IsFavorite { get; set; }

    // 성능과 별개로 개인적으로 좋아하는 캐릭터 표시
    public bool IsBeloved { get; set; }

    // 기본 형태의 대표 이미지입니다.
    public string ImageFileName { get; set; } = string.Empty;

    // 기본 형태에서 매번 선택할 수 있는 문자
    public List<string> Letters { get; set; } = new();

    // 같은 이름을 유지하는 모드시프트 형태입니다.
    // 이름이 달라지는 모드시프트는 기존처럼 별도 캐릭터 + DeckRestrictionGroupId를 사용합니다.
    public List<CharacterForm> AlternateForms { get; set; } = new();

    // 조건 달성, 변신 후 등 상태별 문자
    public List<CharacterLetterState> LetterStates { get; set; } = new();

    // 같은 값이 지정된 캐릭터끼리는 한 덱에 동시에 편성할 수 없습니다.
    // 이름이 다른 모드시프트 전후처럼 별도 캐릭터를 묶는 내부 그룹 ID입니다.
    public string DeckRestrictionGroupId { get; set; } = string.Empty;

    // 이 캐릭터가 덱 1번(리더)일 때 대상 그룹에게 문자를 부여하는 규칙입니다.
    public MiracleLeaderEffect MiracleLeaderEffect { get; set; } = new();

    // 덱에 특정 그룹 캐릭터가 일정 수 이상 편성되면 이 캐릭터 자신에게 문자가 추가되는 규칙입니다.
    public DeckGroupLetterEffect DeckGroupLetterEffect { get; set; } = new();

    // 전투 중 1회만 쓸 수 있는 문자
    public List<string> OneTimeLetters { get; set; } = new();

    // 이미 사용한 1회 한정 문자. 추후 UI에서 체크하도록 확장할 수 있습니다.
    public List<string> UsedOneTimeLetters { get; set; } = new();

    // 검색할 때만 사용하는 현재 문자 상태. JSON에는 저장하지 않습니다.
    [JsonIgnore]
    public string? ActiveLetterStateId { get; set; }

    // 검색할 때만 사용하는 현재 동일명 모드시프트 형태입니다.
    [JsonIgnore]
    public string? ActiveFormId { get; set; }

    // 현재 리더 효과로 추가된 문자. 검색용 복제본에만 설정합니다.
    [JsonIgnore]
    public List<string> ActiveMiracleGrantedLetters { get; set; } = new();

    [JsonIgnore]
    public string ActiveMiracleLeaderName { get; set; } = string.Empty;

    [JsonIgnore]
    public string ActiveMiracleEffectNote { get; set; } = string.Empty;

    [JsonIgnore]
    public List<string> ActiveDeckGroupGrantedLetters { get; set; } = new();

    [JsonIgnore]
    public string ActiveDeckGroupConditionText { get; set; } = string.Empty;

    [JsonIgnore]
    public string ActiveDeckGroupEffectNote { get; set; } = string.Empty;

    public HashSet<string> GetAvailableLetters(string? stateId = null)
    {
        HashSet<string> result = GetOwnAvailableLetters(stateId);
        foreach (string letter in ActiveMiracleGrantedLetters ?? new List<string>())
        {
            string normalized = KanaUtility.NormalizeCell(letter);
            if (normalized.Length > 0)
            {
                result.Add(normalized);
            }
        }

        foreach (string letter in ActiveDeckGroupGrantedLetters ?? new List<string>())
        {
            string normalized = KanaUtility.NormalizeCell(letter);
            if (normalized.Length > 0)
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    public HashSet<string> GetOwnAvailableLetters(string? stateId = null)
    {
        string resolvedStateId = string.IsNullOrWhiteSpace(stateId)
            ? ActiveLetterStateId ?? BaseLetterStateId
            : stateId;

        HashSet<string> baseLetters = GetFormBaseAvailableLetters(ActiveFormId);
        if (string.IsNullOrWhiteSpace(resolvedStateId) ||
            string.Equals(resolvedStateId, BaseLetterStateId, StringComparison.Ordinal))
        {
            return baseLetters;
        }

        CharacterLetterState? state = (LetterStates ?? new List<CharacterLetterState>()).FirstOrDefault(item =>
            string.Equals(item.Id, resolvedStateId, StringComparison.Ordinal));
        if (state is null)
        {
            return baseLetters;
        }

        var result = state.IncludeBaseLetters
            ? new HashSet<string>(baseLetters, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        foreach (string letter in state.Letters)
        {
            string normalized = KanaUtility.NormalizeCell(letter);
            if (normalized.Length > 0)
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    public bool UsesMiracleGrantedLetter(string letter, string? stateId = null)
    {
        string normalized = KanaUtility.NormalizeCell(letter);
        if (normalized.Length == 0 ||
            !(ActiveMiracleGrantedLetters ?? new List<string>()).Contains(normalized, StringComparer.Ordinal))
        {
            return false;
        }

        return !GetOwnAvailableLetters(stateId).Contains(normalized);
    }

    public bool UsesDeckGroupGrantedLetter(string letter, string? stateId = null)
    {
        string normalized = KanaUtility.NormalizeCell(letter);
        if (normalized.Length == 0 ||
            !(ActiveDeckGroupGrantedLetters ?? new List<string>()).Contains(normalized, StringComparer.Ordinal))
        {
            return false;
        }

        return !GetOwnAvailableLetters(stateId).Contains(normalized) &&
               !(ActiveMiracleGrantedLetters ?? new List<string>()).Contains(normalized, StringComparer.Ordinal);
    }

    public CharacterLetterState? FindLetterState(string? stateId)
        => string.IsNullOrWhiteSpace(stateId) ||
           string.Equals(stateId, BaseLetterStateId, StringComparison.Ordinal)
            ? null
            : (LetterStates ?? new List<CharacterLetterState>()).FirstOrDefault(item =>
                string.Equals(item.Id, stateId, StringComparison.Ordinal));

    public CharacterForm? FindForm(string? formId)
        => string.IsNullOrWhiteSpace(formId) ||
           string.Equals(formId, BaseFormId, StringComparison.Ordinal) ||
           string.Equals(formId, AllFormsId, StringComparison.Ordinal)
            ? null
            : (AlternateForms ?? new List<CharacterForm>()).FirstOrDefault(item =>
                string.Equals(item.Id, formId, StringComparison.Ordinal));

    public string GetLetterStateName(string? stateId)
    {
        CharacterLetterState? state = FindLetterState(stateId);
        return state is null ? "기본" : state.Name;
    }

    public string GetFormName(string? formId)
    {
        if (string.Equals(formId, AllFormsId, StringComparison.Ordinal))
        {
            return "전체 형태";
        }

        CharacterForm? form = FindForm(formId);
        return form is null ? "기본 형태" : form.Name;
    }

    public string GetActiveLetterStateName()
        => GetLetterStateName(ActiveLetterStateId);

    public string GetActiveLetterStateKind()
        => FindLetterState(ActiveLetterStateId)?.Kind ?? string.Empty;

    public string GetActiveFormName()
        => GetFormName(ActiveFormId);

    public string GetActiveImageFileName()
    {
        CharacterForm? form = FindForm(ActiveFormId);
        return form is not null && !string.IsNullOrWhiteSpace(form.ImageFileName)
            ? form.ImageFileName
            : ImageFileName;
    }

    public CharacterForm? ResolveFormForLetter(string letter)
    {
        string normalized = KanaUtility.NormalizeCell(letter);
        if (normalized.Length == 0)
        {
            return null;
        }

        if (!string.Equals(ActiveFormId, AllFormsId, StringComparison.Ordinal))
        {
            CharacterForm? activeForm = FindForm(ActiveFormId);
            return activeForm is not null && NormalizeLetters(activeForm.Letters).Contains(normalized)
                ? activeForm
                : null;
        }

        // 전체 형태 검색에서는 기본 형태를 먼저 사용하고, 기본에 없을 때 추가 형태를 찾습니다.
        if (NormalizeLetters(Letters).Contains(normalized))
        {
            return null;
        }

        return (AlternateForms ?? new List<CharacterForm>()).FirstOrDefault(form =>
            NormalizeLetters(form.Letters).Contains(normalized));
    }

    public bool UsesAlternateFormForLetter(string letter)
        => ResolveFormForLetter(letter) is not null;

    public bool UsesSpecialLetterState
        => FindLetterState(ActiveLetterStateId) is not null;

    public bool HasAlternateForms
        => (AlternateForms ?? new List<CharacterForm>()).Count > 0;

    public bool HasActiveMiracleGrant
        => ActiveMiracleGrantedLetters.Count > 0 && ActiveMiracleLeaderName.Length > 0;

    public bool HasActiveDeckGroupGrant
        => ActiveDeckGroupGrantedLetters.Count > 0 && ActiveDeckGroupConditionText.Length > 0;

    private HashSet<string> GetFormBaseAvailableLetters(string? formId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        if (string.Equals(formId, AllFormsId, StringComparison.Ordinal))
        {
            AddLetters(result, Letters);
            foreach (CharacterForm form in AlternateForms ?? new List<CharacterForm>())
            {
                AddLetters(result, form.Letters);
            }
        }
        else
        {
            CharacterForm? form = FindForm(formId);
            AddLetters(result, form?.Letters ?? Letters);
        }

        var used = UsedOneTimeLetters
            .Select(KanaUtility.NormalizeCell)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string letter in OneTimeLetters)
        {
            string normalized = KanaUtility.NormalizeCell(letter);
            if (normalized.Length > 0 && !used.Contains(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static void AddLetters(HashSet<string> target, IEnumerable<string>? letters)
    {
        foreach (string letter in letters ?? Array.Empty<string>())
        {
            string normalized = KanaUtility.NormalizeCell(letter);
            if (normalized.Length > 0)
            {
                target.Add(normalized);
            }
        }
    }

    private static HashSet<string> NormalizeLetters(IEnumerable<string>? letters)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        AddLetters(result, letters);
        return result;
    }
}
