using System.Text;
using System.Text.RegularExpressions;

namespace KotodamanWordFinder.Utilities;

/// <summary>
/// 캐릭터 이름 검색 보조 별칭을 만듭니다.
/// 가나 이름은 한글 독음으로 자동 변환하고, 한자로만 적힌 이름은
/// 자주 쓰는 고유명사 조각을 한글 검색어로 보완합니다.
/// 사용자가 직접 등록한 SearchAliases가 항상 최우선의 범용 해결책입니다.
/// </summary>
public static class CharacterSearchAliasUtility
{
    // 현재 DB에서 한글 검색 수요가 특히 높은 귀멸의 칼날 이름 조각.
    // 전체 이름을 하드코딩하지 않고 성/이름 조각으로 나눠 복합 캐릭터에도 재사용합니다.
    private static readonly Regex KanaRunRegex = new(
        "[ぁ-ゖァ-ヺー]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, string> KnownJapaneseNameParts =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["竈門"] = "카마도",
            ["炭治郎"] = "탄지로",
            ["禰豆子"] = "네즈코",
            ["時透"] = "토키토",
            ["無一郎"] = "무이치로",
            ["甘露寺"] = "칸로지",
            ["蜜璃"] = "미츠리",
            ["胡蝶"] = "코쵸",
            ["冨岡"] = "토미오카",
            ["富岡"] = "토미오카",
            ["義勇"] = "기유",
            ["不死川"] = "시나즈가와",
            ["実弥"] = "사네미",
            ["玄弥"] = "겐야",
            ["伊黒"] = "이구로",
            ["小芭内"] = "오바나이",
            ["半天狗"] = "한텐구",
            ["嘴平"] = "하시비라",
            ["伊之助"] = "이노스케",
            ["堕姫"] = "다키",
            ["妓夫太郎"] = "규타로",
            ["宇髄"] = "우즈이",
            ["天元"] = "텐겐",
            ["煉獄"] = "렌고쿠",
            ["杏寿郎"] = "쿄쥬로",
            ["槇寿郎"] = "신쥬로",
            ["悲鳴嶼"] = "히메지마",
            ["行冥"] = "교메이",
            ["我妻"] = "아가츠마",
            ["善逸"] = "젠이츠",
            ["栗花落"] = "츠유리",
            ["狛治"] = "하쿠지",
            ["恋雪"] = "코유키",
            ["猗窩座"] = "아카자",
            ["獪岳"] = "카이가쿠",
            ["玉壺"] = "굣코",
            ["産屋敷"] = "우부야시키",
            ["輝利哉"] = "키리야",
            ["童磨"] = "도우마",
            ["累"] = "루이",
            ["縁壱"] = "요리이치",
            ["零式"] = "영식",
            ["錆兎"] = "사비토",
            ["真菰"] = "마코모",
            ["響凱"] = "쿄우가이",
            ["鬼舞辻"] = "키부츠지",
            ["無惨"] = "무잔",
            ["魘夢"] = "엔무"
        };

    public static IReadOnlyList<string> BuildAutomaticAliases(string? name)
    {
        string normalized = (name ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (normalized.Length == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            string alias = (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
            if (alias.Length > 0 && seen.Add(alias))
            {
                result.Add(alias);
            }
        }

        foreach ((string japanese, string korean) in KnownJapaneseNameParts)
        {
            if (normalized.Contains(japanese, StringComparison.Ordinal))
            {
                Add(korean);
            }
        }

        // 이름 안의 가나 구간은 자동으로 한글 독음 후보를 만듭니다.
        // 예: しのぶ -> 시노부, カナヲ -> 카나오.
        foreach (Match match in KanaRunRegex.Matches(normalized))
        {
            string korean = KanaUtility.ConvertKanaToHangul(match.Value);
            Add(korean);

            // 장음/연속 모음 표기 차이 때문에 한글에서 한 음절을 줄여 쓰는 경우를 위한 보조 후보.
            string compact = CompactKoreanLongVowels(korean);
            Add(compact);
        }

        return result;
    }

    private static string CompactKoreanLongVowels(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // 일반적인 일본어 장음 한글 표기에서 '우/이'가 한 번 더 붙는 경우를 완화합니다.
        // 원본 후보도 함께 유지하므로 과도한 치환으로 검색이 사라지지는 않습니다.
        return value
            .Replace("오우", "오", StringComparison.Ordinal)
            .Replace("우우", "우", StringComparison.Ordinal)
            .Replace("에이", "에", StringComparison.Ordinal)
            .Replace("이이", "이", StringComparison.Ordinal);
    }

}
