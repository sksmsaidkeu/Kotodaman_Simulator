namespace KotodamanWordFinder.Models;

public static class CharacterCategories
{
    public const string Special = "스페셜";
    public const string Legend = "레전드";
    public const string Grand = "그랜드";
    public const string Dream = "드림";
    public const string Miracle = "미라클";
    public const string Original = "오리지널";
    public const string Collaboration = "콜라보";
    public const string Other = "기타";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Special,
        Legend,
        Grand,
        Dream,
        Miracle,
        Original,
        Collaboration,
        Other
    };

    public static string Normalize(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return Other;
        }

        string trimmed = category.Trim();
        return All.Contains(trimmed, StringComparer.Ordinal)
            ? trimmed
            : Other;
    }

    public static int GetSortOrder(string? category)
        => Normalize(category) switch
        {
            Special => 0,
            Legend => 1,
            Grand => 2,
            Dream => 3,
            Miracle => 4,
            Original => 5,
            Collaboration => 6,
            _ => 7
        };
}
