namespace KotodamanWordFinder.Models;

public static class CharacterLetterStateKinds
{
    public const string Conditional = "조건부 추가";
    public const string Transform = "변신 후";
    public const string Other = "기타 상태";

    // v1.9.1까지 저장된 모드시프트 상태는 독립 캐릭터로 관리하는 새 구조에 맞춰
    // 기타 상태로 안전하게 이전합니다. 데이터 자체는 삭제하지 않습니다.
    private const string LegacyModeShift = "모드시프트";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Conditional,
        Transform,
        Other
    };

    public static string Normalize(string? value)
    {
        if (string.Equals(value, LegacyModeShift, StringComparison.Ordinal))
        {
            return Other;
        }

        return All.Contains(value ?? string.Empty, StringComparer.Ordinal)
            ? value!
            : Other;
    }
}
