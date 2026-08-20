using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder.Services;

public sealed class CharacterWebImportService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly TimeSpan GameWithRequestTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan DatabaseRequestTimeout = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan DatabaseEnrichmentTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ImageRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly Regex CharacterLinkRegex = new(
        "<a\\b(?<attrs>[^>]*?href=[\\\"'](?<url>(?:https?://(?:www\\.)?kotodaman-db\\.com)?/character/\\d+/?(?:[?#][^\\\"']*)?)[\\\"'][^>]*)>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CharacterUrlRegex = new(
        @"(?<url>(?:https?://(?:www\.)?kotodaman-db\.com)?/character/\d+/?(?:[?#][^""'<>\s]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GameWithArticleLinkRegex = new(
        "<a\\b(?<attrs>[^>]*?href=[\\\"'](?<url>(?:(?:https?:)?//(?:www\\.)?gamewith\\.jp)?/kotodaman/article/show/\\d+(?:[?#][^\\\"']*)?)[\\\"'][^>]*)>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTableRegex = new(
        "<table\\b[^>]*>.*?</table>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlRowRegex = new(
        "<tr\\b[^>]*>(?<row>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlCellRegex = new(
        "<t[dh]\\b[^>]*>(?<cell>.*?)</t[dh]>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly string[] GameWithCollaborationGroupMarkers =
    {
        "友情コンボ", "ヘスティアファミリアの絆", "心の怪盗団", "鬼滅の刃", "奇跡の連携",
        "シャーマン", "仮面ライダー", "東京卍會", "魔物", "マクロスΔ＆F", "ハイキュー!!",
        "鋼の錬金術師", "銀魂", "タイバニ", "エヴァンゲリオン", "呪術廻戦", "幽☆遊☆白書",
        "五等分の花嫁", "炎炎ノ消防隊", "ウルトラマン", "SPY×FAMILY", "ボーダー",
        "ゴールデンカムイ", "チェンソーマン", "ブルーロック", "文スト", "オーバーロード",
        "ヒロアカ", "デジモン", "物語シリーズ", "天竺", "シャンフロ", "新テニ",
        "サカモトデイズ", "ダンダダン", "怪獣８号", "推しの子", "リゼロ",
        "スーパー戦隊", "フリーレン", "プリキュア", "第七王子", "ぼっちざろっく",
        "このすば", "薬屋のひとりごと", "とある科学の超電磁砲", "桃源暗鬼",
        "蟲神器", "ワンパンマン", "東京喰種", "転スラ", "キングダム"
    };

    private static readonly string[] GameWithOriginalGroupMarkers =
    {
        "いにしえの記憶", "新たなる希望", "夏の思い出", "無言の影", "斬の戦律",
        "砲の戦律", "突の戦律", "重の戦律", "超の戦律", "打の戦律", "全の戦律",
        "セイユニマ", "ブリタンディ", "リザンテクス", "此方へ", "三国の願い",
        "廻る魂", "月の夢", "『夢』への旅路", "おたすけ天使", "彩々のひと"
    };

    // 세이유니마 이후에 추가된 오리지널 계열입니다.
    // GameWith 목록 평가가 A로 낮아진 최신 6성도 이 그룹에 속하면 후보로 유지합니다.
    private static readonly string[] RecentOriginalGroupMarkers =
    {
        "セイユニマ", "ブリタンディ", "リザンテクス", "此方へ", "三国の願い",
        "廻る魂", "月の夢", "『夢』への旅路", "おたすけ天使", "彩々のひと"
    };

    // 세이유니마 초기 캐릭터의 GameWith 문서 번호대입니다.
    // 그룹 메타데이터가 없는 최신 오리지널도 이 번호 이상이면 세이유니마 이후 후보로 봅니다.
    private const int SeiyunimaEraArticleIdFloor = 343000;

    public async Task<IReadOnlyList<GameWithCharacterLink>> DiscoverGameWithRatedCharacterLinksAsync(
        string sourceUrl,
        IReadOnlyCollection<string> allowedRatings,
        GameWithRatingMatchMode matchMode,
        bool originalOnly,
        bool includeRecentSixStarA,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(sourceUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri is null ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !uri.Host.EndsWith("gamewith.jp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GameWith 전 캐릭터 목록 주소를 입력하세요.");
        }

        string html = await GetHtmlAsync(uri, cancellationToken);
        if (!IsGameWithAllCharactersListPage(uri, html))
        {
            throw new InvalidOperationException("이 기능은 GameWith '전 캐릭터 목록' 페이지에서만 사용할 수 있습니다.");
        }

        var ratings = (allowedRatings ?? Array.Empty<string>())
            .Select(NormalizeGameWithRating)
            .Where(rating => rating.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ratings.Count == 0)
        {
            throw new InvalidOperationException("검색할 평가를 하나 이상 선택하세요.");
        }

        var found = new Dictionary<string, GameWithCharacterLink>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tableMatch in HtmlTableRegex.Matches(html))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string tableHtml = tableMatch.Value;
            string tableText = CleanText(tableHtml);
            bool isCharacterRatingTable =
                tableText.Contains("キャラ", StringComparison.Ordinal) &&
                tableText.Contains("属性", StringComparison.Ordinal) &&
                tableText.Contains("文字", StringComparison.Ordinal) &&
                tableText.Contains("サブ評価", StringComparison.Ordinal) &&
                tableText.Contains("リーダー評価", StringComparison.Ordinal);
            if (!isCharacterRatingTable)
            {
                continue;
            }

            foreach (Match rowMatch in HtmlRowRegex.Matches(tableHtml))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Match[] cells = HtmlCellRegex.Matches(rowMatch.Groups["row"].Value)
                    .Cast<Match>()
                    .ToArray();
                if (cells.Length < 6)
                {
                    continue;
                }

                string subRating = NormalizeGameWithRating(CleanText(cells[^2].Groups["cell"].Value));
                string leaderRating = NormalizeGameWithRating(CleanText(cells[^1].Groups["cell"].Value));
                string attributeHint = cells.Length > 1
                    ? DeckDataService.NormalizeAttribute(CleanText(cells[1].Groups["cell"].Value))
                    : string.Empty;
                string lettersHint = cells.Length > 2
                    ? ExtractGameWithRatingTableLetters(cells[2].Groups["cell"].Value)
                    : string.Empty;

                string firstCellHtml = cells[0].Groups["cell"].Value;
                Match linkMatch = GameWithArticleLinkRegex.Match(firstCellHtml);
                if (!linkMatch.Success)
                {
                    continue;
                }

                string normalizedUrl = NormalizeGameWithArticleUrl(linkMatch.Groups["url"].Value, uri);
                if (normalizedUrl.Length == 0)
                {
                    continue;
                }

                bool subMatch = ratings.Contains(subRating);
                bool leaderMatch = ratings.Contains(leaderRating);
                bool ratingMatch = MatchesRatingMode(matchMode, subMatch, leaderMatch);

                string groupHint = InferGameWithOriginalGroupFromRow(rowMatch.Value);
                bool subIsA = string.Equals(subRating, "A", StringComparison.OrdinalIgnoreCase);
                bool leaderIsA = string.Equals(leaderRating, "A", StringComparison.OrdinalIgnoreCase);
                bool recentSixStarCandidate =
                    includeRecentSixStarA &&
                    originalOnly &&
                    MatchesRatingMode(matchMode, subIsA, leaderIsA) &&
                    (IsRecentOriginalGroup(groupHint) || IsSeiyunimaEraArticleUrl(normalizedUrl));

                if (!ratingMatch && !recentSixStarCandidate)
                {
                    continue;
                }

                string attrs = linkMatch.Groups["attrs"].Value;
                string innerHtml = linkMatch.Groups["text"].Value;
                string bestLabel = new[]
                {
                    CleanText(ExtractAttribute(attrs, "title")),
                    CleanText(ExtractAttribute(attrs, "aria-label")),
                    ExtractFirstImageAlt(innerHtml),
                    CleanText(innerHtml)
                }.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                string nameHint = CleanGameWithLinkLabel(bestLabel);
                if (IsGenericGameWithLinkLabel(nameHint))
                {
                    continue;
                }

                bool? collaboration = InferGameWithCollaborationFromRow(rowMatch.Value);

                var link = new GameWithCharacterLink(
                    normalizedUrl,
                    nameHint,
                    groupHint,
                    subRating,
                    leaderRating,
                    collaboration,
                    attributeHint,
                    lettersHint,
                    RequiresRecentSixStarValidation: !ratingMatch && recentSixStarCandidate);

                if (!found.TryGetValue(normalizedUrl, out GameWithCharacterLink? existing) ||
                    existing is null ||
                    GetRatedLinkInformationScore(link) > GetRatedLinkInformationScore(existing))
                {
                    found[normalizedUrl] = link;
                }
            }
        }

        if (found.Count == 0)
        {
            throw new InvalidOperationException("선택한 평가 조건에 맞는 캐릭터를 찾지 못했습니다.");
        }

        return found.Values
            .OrderByDescending(link => GetRatingSortValue(link.SubRating))
            .ThenByDescending(link => GetRatingSortValue(link.LeaderRating))
            .ThenBy(link => link.NameHint, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<GameWithCharacterLink>> DiscoverGameWithCharacterLinksAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? knownGroups = null)
    {
        if (!Uri.TryCreate(sourceUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri is null ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !uri.Host.EndsWith("gamewith.jp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("GameWith 주소를 입력하세요.");
        }

        string html = await GetHtmlAsync(uri, cancellationToken);

        if (IsGameWithAllCharactersListPage(uri, html))
        {
            throw new InvalidOperationException(
                "GameWith '전 캐릭터 목록'은 브라우저에서 선택한 그룹 필터가 URL/서버 HTML에 반영되지 않아 프로그램에서는 전체 캐릭터로 보입니다. " +
                "따라서 3000개 이상을 잘못 가져오는 것을 막기 위해 중단했습니다. 귀멸의 칼날처럼 특정 콜라보만 등록할 때는 해당 '콜라보 정보 정리' 페이지 주소를 넣어 주세요.");
        }

        if (LooksLikeGameWithCharacterPage(html))
        {
            IReadOnlyList<string> lines = HtmlToLines(html);
            string name = ExtractGameWithCharacterName(html, lines);
            return new[] { new GameWithCharacterLink(NormalizeGameWithArticleUrl(uri.ToString(), uri), name) };
        }

        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 콜라보 정리 페이지는 페이지 전체를 훑지 않습니다.
        // GameWith 페이지에는 사이드바/최신 캐릭터/다른 콜라보 링크가 함께 들어 있으므로,
        // "○○キャラ一覧"처럼 실제 캐릭터 명단이 나열된 연속 h2 섹션만 잘라서 처리합니다.
        string characterListSections = ExtractGameWithCharacterListSections(html);
        if (characterListSections.Length > 0)
        {
            AddGameWithLinksFromFragment(found, characterListSections, uri, acceptPlainLabels: true);
        }
        else
        {
            // 캐릭터 목록 섹션을 찾지 못한 오래된/다른 형식의 페이지는 평가표를 먼저 사용합니다.
            foreach (Match tableMatch in HtmlTableRegex.Matches(html))
            {
                string tableHtml = tableMatch.Value;
                string tableText = CleanText(tableHtml);
                bool characterTable =
                    tableText.Contains("サブ評価", StringComparison.Ordinal) &&
                    tableText.Contains("リーダー評価", StringComparison.Ordinal);
                if (!characterTable)
                {
                    continue;
                }

                AddGameWithLinksFromFragment(found, tableHtml, uri, acceptPlainLabels: true);
            }

            // 마지막 호환 경로. h1 이후 실제 기사 본문만 사용하고 관련 링크 섹션 앞에서 끊습니다.
            string mainContent = ExtractGameWithMainContent(html);
            AddGameWithLinksFromFragment(found, mainContent, uri, acceptPlainLabels: true);
        }

        // 잘못된 본문 판별로 사이트 전체 링크가 섞이는 상황을 방지합니다.
        if (found.Count > 300)
        {
            throw new InvalidOperationException(
                $"이 페이지에서 캐릭터 후보가 {found.Count}개나 발견되어 안전을 위해 중단했습니다. " +
                "콜라보 정리 페이지를 사용하거나 개별 캐릭터 URL을 여러 줄로 붙여 넣어 주세요.");
        }

        string normalizedSource = NormalizeGameWithArticleUrl(uri.ToString(), uri);
        found.Remove(normalizedSource);

        string groupHint = InferGameWithListPageGroupHint(html, knownGroups);
        return found
            .Select(item => new GameWithCharacterLink(item.Key, item.Value, groupHint))
            .OrderBy(item => item.NameHint, StringComparer.Ordinal)
            .ThenBy(item => item.Url, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeGameWithRating(string? value)
    {
        string text = CleanText(value ?? string.Empty).ToUpperInvariant();
        Match match = Regex.Match(text, @"(?<![A-Z])(SSS|SS|S|A|B|C|D|E|F)(?![A-Z])", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static int GetRatingSortValue(string? rating)
        => NormalizeGameWithRating(rating) switch
        {
            "SSS" => 9,
            "SS" => 8,
            "S" => 7,
            "A" => 6,
            "B" => 5,
            "C" => 4,
            "D" => 3,
            "E" => 2,
            "F" => 1,
            _ => 0
        };

    private static bool MatchesRatingMode(
        GameWithRatingMatchMode matchMode,
        bool subMatch,
        bool leaderMatch)
        => matchMode switch
        {
            GameWithRatingMatchMode.SubOnly => subMatch,
            GameWithRatingMatchMode.LeaderOnly => leaderMatch,
            GameWithRatingMatchMode.Both => subMatch && leaderMatch,
            _ => subMatch || leaderMatch
        };

    private static int GetRatedLinkInformationScore(GameWithCharacterLink link)
    {
        int score = 0;
        if (link.NameHint.Length > 0) score += 2;
        if (link.SubRating.Length > 0) score += 1;
        if (link.LeaderRating.Length > 0) score += 1;
        if (link.AttributeHint.Length > 0) score += 1;
        if (link.LettersHint.Length > 0) score += 2;
        if (link.GroupHint.Length > 0) score += 2;
        if (link.IsCollaboration.HasValue) score += 1;
        if (link.RequiresRecentSixStarValidation) score += 1;
        return score;
    }

    private static string InferGameWithOriginalGroupFromRow(string rowHtml)
    {
        string source = WebUtility.HtmlDecode(rowHtml ?? string.Empty)
            .Normalize(NormalizationForm.FormKC);
        if (source.Length == 0)
        {
            return string.Empty;
        }

        string normalizedSource = NormalizeGroupEvidence(source);
        foreach (string marker in GameWithOriginalGroupMarkers.OrderByDescending(value => value.Length))
        {
            if (source.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                normalizedSource.Contains(
                    NormalizeGroupEvidence(marker),
                    StringComparison.OrdinalIgnoreCase))
            {
                return marker;
            }
        }

        return string.Empty;
    }

    public static bool IsRecentOriginalGroup(string? value)
    {
        string normalized = NormalizeGroupEvidence(value ?? string.Empty);
        return normalized.Length > 0 && RecentOriginalGroupMarkers.Any(marker =>
            normalized.Equals(NormalizeGroupEvidence(marker), StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsSeiyunimaEraArticleUrl(string? url)
    {
        Match match = Regex.Match(
            url ?? string.Empty,
            @"/article/show/(?<id>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success &&
               int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int articleId) &&
               articleId >= SeiyunimaEraArticleIdFloor;
    }

    public static bool IsSixStarRarity(string? value)
    {
        string normalized = (value ?? string.Empty)
            .Normalize(NormalizationForm.FormKC)
            .Replace("★", string.Empty, StringComparison.Ordinal)
            .Replace("☆", string.Empty, StringComparison.Ordinal)
            .Replace("星", string.Empty, StringComparison.Ordinal)
            .Trim();
        return string.Equals(normalized, "6", StringComparison.Ordinal);
    }

    public static bool IsRecentSixStarAEligible(
        CharacterImportData data,
        GameWithCharacterLink link,
        out string reason)
    {
        string group = !string.IsNullOrWhiteSpace(data.GroupName)
            ? data.GroupName
            : link.GroupHint;

        bool recentByGroup = IsRecentOriginalGroup(group);
        bool recentByArticle = IsSeiyunimaEraArticleUrl(link.Url);
        if (!recentByGroup && !recentByArticle)
        {
            reason = group.Length > 0
                ? $"세이유니마 이후 그룹/문서가 아님: {group}"
                : "세이유니마 이후 출시 근거를 확인하지 못함";
            return false;
        }

        if (!IsSixStarRarity(data.Rarity))
        {
            reason = data.Rarity.Length > 0
                ? $"6성이 아님: {data.Rarity}"
                : "6성 여부를 확인하지 못함";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool? InferGameWithCollaborationFromRow(string rowHtml)
    {
        string source = WebUtility.HtmlDecode(rowHtml ?? string.Empty).Normalize(NormalizationForm.FormKC);
        if (source.Length == 0)
        {
            return null;
        }

        // 캐릭터명이나 행의 일반 텍스트에 작품명이 들어 있다는 이유만으로 콜라보로 단정하지 않습니다.
        // GameWith가 행/필터에 명시한 class/data 속성만 강한 근거로 사용합니다.
        foreach (Match tagMatch in Regex.Matches(
                     source,
                     @"<(?:tr|td|a|div|span)\b(?<attrs>[^>]*)>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant))
        {
            string attrs = tagMatch.Groups["attrs"].Value;
            foreach (string attributeName in new[]
                     {
                         "data-collaboration", "data-collab", "data-category", "data-group",
                         "data-filter", "class", "title", "aria-label"
                     })
            {
                string value = CleanText(ExtractAttribute(attrs, attributeName)).Normalize(NormalizationForm.FormKC);
                if (value.Length == 0)
                {
                    continue;
                }

                if (Regex.IsMatch(
                        value,
                        @"(?:^|[\s,;:_\-/])(?:collab(?:o|oration)?|コラボ)(?:$|[\s,;:_\-/])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return true;
                }

                if (Regex.IsMatch(
                        value,
                        @"(?:^|[\s,;:_\-/])(?:original|オリジナル)(?:$|[\s,;:_\-/])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return false;
                }
            }
        }

        return null;
    }

    private static bool? InferGameWithCollaborationStatus(string html, CharacterImportData data)
    {
        string[] groups = new[] { data.GroupName }
            .Concat(data.IncludedGroups ?? new List<string>())
            .Concat(data.GroupCandidates ?? new List<string>())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim().Normalize(NormalizationForm.FormKC))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        bool hasExplicitCollaborationGroup = groups.Any(IsExplicitCollaborationGroupName);
        bool hasExplicitOriginalGroup = groups.Any(IsExplicitOriginalGroupName);

        string articleText = CleanText(ExtractGameWithArticleBodyRange(html));
        string articleLead = articleText.Length > 3500 ? articleText[..3500] : articleText;
        string headingText = string.Join(" ", new[]
        {
            ExtractFirstHeading(html),
            ExtractMetaContent(html, "og:title"),
            ExtractMetaContent(html, "twitter:title")
        });

        if (articleLead.Contains("オリジナルキャラ", StringComparison.Ordinal) ||
            articleLead.Contains("コトダマンオリジナル", StringComparison.Ordinal) ||
            headingText.Contains("オリジナル", StringComparison.Ordinal) ||
            (hasExplicitOriginalGroup && !headingText.Contains("コラボ", StringComparison.Ordinal)))
        {
            return false;
        }

        // 그룹 후보/같이 취급 그룹만으로는 절대 콜라보를 확정하지 않습니다.
        // 특성 문장에 다른 작품 그룹이 등장하는 오리지널 캐릭터도 있기 때문입니다.
        // 제목이나 기사 앞부분에 "작품명 + コラボ"가 직접 명시되어야만 true를 반환합니다.
        foreach (string marker in GameWithCollaborationGroupMarkers)
        {
            string escaped = Regex.Escape(marker);
            bool headingEvidence = Regex.IsMatch(
                headingText,
                escaped + @".{0,12}コラボ|コラボ.{0,12}" + escaped,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool leadEvidence = Regex.IsMatch(
                articleLead,
                escaped + @".{0,18}(?:コラボキャラ|コラボイベント|コラボガチャ|コラボで登場)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if ((headingEvidence || leadEvidence) && (hasExplicitCollaborationGroup || headingEvidence || leadEvidence))
            {
                return true;
            }
        }

        // 그룹이 없다는 사실은 오리지널/콜라보 어느 쪽의 근거도 아닙니다.
        return null;
    }

    private static bool IsExplicitCollaborationGroupName(string value)
    {
        string normalized = NormalizeGroupEvidence(value);
        if (normalized.Length < 2)
        {
            return false;
        }

        return GameWithCollaborationGroupMarkers.Any(marker =>
        {
            string normalizedMarker = NormalizeGroupEvidence(marker);
            return normalized.Equals(normalizedMarker, StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals(normalizedMarker + "コラボ", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith(normalizedMarker + "コラボ第", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsExplicitOriginalGroupName(string value)
    {
        string normalized = NormalizeGroupEvidence(value);
        return normalized.Length >= 2 && GameWithOriginalGroupMarkers.Any(marker =>
            normalized.Equals(NormalizeGroupEvidence(marker), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeGroupEvidence(string value) =>
        Regex.Replace(
            (value ?? string.Empty).Normalize(NormalizationForm.FormKC),
            @"[\s　・･·「」『』【】〖〗\[\]（）()]+",
            string.Empty,
            RegexOptions.CultureInvariant);

    private static string ExtractGameWithRatingTableLetters(string cellHtml)
    {
        var letters = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in HtmlToLines(cellHtml ?? string.Empty))
        {
            AddKanaListLine(letters, line);
        }

        if (letters.Count == 0)
        {
            AddKanaTokens(letters, CleanText(cellHtml ?? string.Empty));
        }

        return string.Join(" · ", letters.OrderBy(letter => letter, StringComparer.Ordinal));
    }

    private static string InferGameWithListPageGroupHint(
        string html,
        IReadOnlyCollection<string>? knownGroups)
    {
        string titleText = string.Join(" ", new[]
        {
            ExtractFirstHeading(html),
            ExtractMetaContent(html, "og:title"),
            ExtractHtmlTitle(html)
        });
        string normalizedTitle = CleanText(titleText).Normalize(NormalizationForm.FormKC);

        string? knownMatch = (knownGroups ?? Array.Empty<string>())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Where(group => normalizedTitle.Contains(group.Normalize(NormalizationForm.FormKC), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(group => group.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(knownMatch))
        {
            return knownMatch;
        }

        // 알려진 그룹 목록에 아직 없는 첫 콜라보를 등록하는 경우도 처리합니다.
        // 예: "【コトダマン】鬼滅の刃コラボ第3弾..." -> "鬼滅の刃"
        string cleaned = Regex.Replace(
            normalizedTitle,
            @"^[【〖\[]?\s*コトダマン\s*[】〗\]]?\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
        Match match = Regex.Match(
            cleaned,
            @"(?<group>[^｜|\-]{2,36}?)コラボ(?:第\d+弾)?",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return string.Empty;
        }

        string candidate = match.Groups["group"].Value.Trim(' ', '　', '【', '】', '〖', '〗');
        return IsReasonableGroupValue(candidate) && !LooksLikeNonGroupLabel(candidate)
            ? candidate
            : string.Empty;
    }

    private static string ExtractGameWithCharacterListSections(string html)
    {
        Match[] headings = Regex.Matches(
                html,
                "<h2\\b[^>]*>.*?</h2>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .Where(match => match.Success && match.Length > 0)
            .ToArray();

        if (headings.Length == 0)
        {
            return string.Empty;
        }

        // 한 페이지 안에 캐릭터 목록 h2가 여러 군데 떨어져 있을 수 있습니다.
        // 예: 가면라이더 정리글은 상단의 "新登場キャラ一覧"와 하단의
        // "登場キャラ一覧" 사이에 가챠/공략 섹션이 끼어 있습니다.
        // 예전에는 첫 번째 목록 블록만 반환해서 최신 탄 10여 명만 발견되는 문제가 있었습니다.
        // 이제 캐릭터 목록으로 판별되는 모든 h2 블록을 각각 잘라 합칩니다.
        var sections = new StringBuilder();

        for (int index = 0; index < headings.Length; index++)
        {
            string headingText = CleanText(headings[index].Value);
            if (!IsGameWithCharacterListHeading(headingText))
            {
                continue;
            }

            int start = headings[index].Index;
            int end = index + 1 < headings.Length
                ? headings[index + 1].Index
                : html.Length;

            if (end <= start)
            {
                continue;
            }

            sections.AppendLine(html.Substring(start, end - start));
        }

        return sections.ToString();
    }

    private static bool IsGameWithCharacterListHeading(string headingText)
    {
        string normalized = CleanText(headingText)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("　", string.Empty, StringComparison.Ordinal);

        if (!normalized.Contains("キャラ一覧", StringComparison.Ordinal))
        {
            return false;
        }

        string[] listKinds =
        {
            "コラボ", "ガチャ", "パック", "配布", "イベント", "降臨", "報酬", "交換", "登場"
        };
        return listKinds.Any(kind => normalized.Contains(kind, StringComparison.Ordinal));
    }

    private static string ExtractGameWithMainContent(string html)
    {
        string articleBody = ExtractGameWithArticleBodyRange(html);
        if (articleBody.Length > 0)
        {
            return articleBody;
        }

        Match[] articleMatches = Regex.Matches(
                html,
                "<article\\b[^>]*>.*?</article>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .Where(match => match.Success && match.Length > 0)
            .ToArray();
        if (articleMatches.Length > 0)
        {
            Match bestArticle = articleMatches
                .OrderByDescending(match => GetGameWithMainContentScore(match.Value))
                .ThenByDescending(match => match.Length)
                .First();
            if (GetGameWithMainContentScore(bestArticle.Value) > 0)
            {
                return bestArticle.Value;
            }
        }

        Match[] mainMatches = Regex.Matches(
                html,
                "<main\\b[^>]*>.*?</main>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .Where(match => match.Success && match.Length > 0)
            .ToArray();
        if (mainMatches.Length > 0)
        {
            return mainMatches
                .OrderByDescending(match => GetGameWithMainContentScore(match.Value))
                .ThenByDescending(match => match.Length)
                .First()
                .Value;
        }

        return html;
    }

    private static string ExtractGameWithArticleBodyRange(string html)
    {
        Match titleHeading = Regex.Match(
            html,
            "<h1\\b[^>]*>.*?</h1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!titleHeading.Success)
        {
            return string.Empty;
        }

        int start = titleHeading.Index;
        Match[] laterH2 = Regex.Matches(
                html.Substring(start),
                "<h2\\b[^>]*>.*?</h2>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline)
            .Cast<Match>()
            .ToArray();

        foreach (Match heading in laterH2)
        {
            string headingText = CleanText(heading.Value);
            if (!headingText.Contains("コトダマンの関連リンク", StringComparison.Ordinal))
            {
                continue;
            }

            int end = start + heading.Index;
            return end > start
                ? html.Substring(start, end - start)
                : string.Empty;
        }

        return html.Substring(start);
    }

    private static int GetGameWithMainContentScore(string htmlFragment)
    {
        string text = CleanText(htmlFragment);
        int score = Math.Min(htmlFragment.Length / 20000, 20);
        if (text.Contains("コトダマン", StringComparison.Ordinal)) score += 10;
        if (text.Contains("目次", StringComparison.Ordinal)) score += 10;
        if (text.Contains("評価", StringComparison.Ordinal)) score += 10;
        if (text.Contains("キャラ", StringComparison.Ordinal)) score += 10;
        return score;
    }

    private static void AddGameWithLinksFromFragment(
        IDictionary<string, string> found,
        string htmlFragment,
        Uri baseUri,
        bool acceptPlainLabels)
    {
        foreach (Match match in GameWithArticleLinkRegex.Matches(htmlFragment))
        {
            string attributes = match.Groups["attrs"].Value;
            string innerHtml = match.Groups["text"].Value;
            string label = CleanText(innerHtml);
            string title = CleanText(ExtractAttribute(attributes, "title"));
            string aria = CleanText(ExtractAttribute(attributes, "aria-label"));
            string imageAlt = ExtractFirstImageAlt(innerHtml);
            string bestLabel = new[] { title, aria, imageAlt, label }
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

            bool evaluationLink =
                bestLabel.Contains("の評価", StringComparison.Ordinal) ||
                bestLabel.Contains("評価はこちら", StringComparison.Ordinal) ||
                bestLabel.EndsWith("評価", StringComparison.Ordinal);
            if (!acceptPlainLabels && !evaluationLink)
            {
                continue;
            }

            if (acceptPlainLabels && !evaluationLink && !IsLikelyCharacterLinkLabel(bestLabel))
            {
                continue;
            }

            string normalizedUrl = NormalizeGameWithArticleUrl(match.Groups["url"].Value, baseUri);
            if (normalizedUrl.Length == 0)
            {
                continue;
            }

            string nameHint = CleanGameWithLinkLabel(bestLabel);
            if (IsGenericGameWithLinkLabel(nameHint))
            {
                continue;
            }

            if (!found.TryGetValue(normalizedUrl, out string? existing) ||
                (string.IsNullOrEmpty(existing) && nameHint.Length > 0))
            {
                found[normalizedUrl] = nameHint;
            }
        }
    }

    private static string ExtractFirstImageAlt(string htmlFragment)
    {
        Match match = Regex.Match(
            htmlFragment,
            "<img\\b(?<attrs>[^>]*)>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return string.Empty;
        }

        return CleanText(ExtractAttribute(match.Groups["attrs"].Value, "alt"));
    }

    private static bool IsGameWithAllCharactersListPage(Uri uri, string html)
    {
        if (uri.AbsolutePath.EndsWith("/article/show/99665", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string text = CleanText(html);
        return text.Contains("全キャラ評価一覧", StringComparison.Ordinal) &&
               text.Contains("キャラを条件で絞り込む", StringComparison.Ordinal) &&
               text.Contains("サブ評価", StringComparison.Ordinal) &&
               text.Contains("リーダー評価", StringComparison.Ordinal);
    }

    private static bool IsLikelyCharacterLinkLabel(string value)
    {
        string cleaned = CleanGameWithLinkLabel(value);
        if (IsGenericGameWithLinkLabel(cleaned))
        {
            return false;
        }

        string normalized = cleaned.Replace(" ", string.Empty, StringComparison.Ordinal);
        string[] articleWords =
        {
            "はこちら", "コラボ情報", "コラボ最新情報", "ガチャ", "当たり", "引くべき", "攻略",
            "クエスト", "交換所", "探索", "ミッション", "イベント", "開催", "報酬", "一覧",
            "ランキング", "解説", "やるべき", "最新情報", "まとめ"
        };
        return normalized.Length > 0 &&
               !articleWords.Any(word => normalized.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeGameWithCharacterPage(string html)
        => html.Contains("基本情報", StringComparison.Ordinal) &&
           (html.Contains("種族", StringComparison.Ordinal) || html.Contains("属性", StringComparison.Ordinal)) &&
           (html.Contains("文字の使いやすさ", StringComparison.Ordinal) ||
            html.Contains("ステータス", StringComparison.Ordinal) ||
            html.Contains("わざ", StringComparison.Ordinal));

    private static string NormalizeGameWithArticleUrl(string value, Uri baseUri)
    {
        string decoded = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        if (decoded.Length == 0)
        {
            return string.Empty;
        }

        if (decoded.StartsWith("//", StringComparison.Ordinal))
        {
            decoded = "https:" + decoded;
        }

        if (!Uri.TryCreate(decoded, UriKind.Absolute, out Uri? absolute))
        {
            if (!Uri.TryCreate(baseUri, decoded, out absolute))
            {
                return string.Empty;
            }
        }

        if (absolute is null || !absolute.Host.EndsWith("gamewith.jp", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var builder = new UriBuilder(absolute)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.ToString();
    }

    private static string CleanGameWithLinkLabel(string value)
    {
        string cleaned = CleanText(value ?? string.Empty).Trim();
        cleaned = Regex.Replace(cleaned, "^[【〖\\[]?コトダマン[】〗\\]]?", string.Empty, RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, "(?:の)?評価(?:とステータス)?(?:はこちら)?[！!。]?$", string.Empty, RegexOptions.IgnoreCase).Trim();
        return cleaned;
    }

    private static bool IsGenericGameWithLinkLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        string normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        string[] generic =
        {
            "全キャラ", "全キャラ一覧", "全キャラ評価一覧", "キャラ一覧", "最強キャラ", "キャラ検索DB",
            "リーダー特性検索", "コラボ一覧", "降臨一覧", "攻略", "まとめ", "ランキング"
        };
        return generic.Any(item => normalized.Contains(item, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CharacterImportData> ImportAsync(
        string sourceUrl,
        bool enrichFromDatabase,
        string? databaseOverrideUrl = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? knownGroups = null,
        IReadOnlyDictionary<string, string[]>? knownGroupRelations = null)
    {
        if (!Uri.TryCreate(sourceUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri is null ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("올바른 캐릭터 페이지 주소를 입력하세요.");
        }

        string host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("gamewith.jp", StringComparison.Ordinal))
        {
            string html = await GetHtmlAsync(uri, cancellationToken);
            CharacterImportData data = ParseGameWith(uri.ToString(), html, knownGroups, knownGroupRelations);
            if (enrichFromDatabase && data.Name.Length > 0)
            {
                await TryEnrichFromDatabaseAsync(
                    data,
                    databaseOverrideUrl,
                    cancellationToken,
                    DatabaseEnrichmentTimeout);
            }

            return data;
        }

        if (host.EndsWith("kotodaman-db.com", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("현재 반자동 등록은 GameWith 캐릭터 페이지를 기준으로 동작합니다.");
        }

        throw new InvalidOperationException("GameWith의 개별 캐릭터 페이지 주소를 입력하세요.");
    }

    public async Task<bool> TryEnrichFromDatabaseAsync(
        CharacterImportData data,
        string? databaseOverrideUrl = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (data is null || string.IsNullOrWhiteSpace(data.Name))
        {
            return false;
        }

        TimeSpan effectiveTimeout = timeout.GetValueOrDefault(DatabaseEnrichmentTimeout);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(effectiveTimeout);

        try
        {
            CharacterImportData? databaseData;
            if (!string.IsNullOrWhiteSpace(databaseOverrideUrl))
            {
                databaseData = await ImportDatabaseOverrideAsync(
                    databaseOverrideUrl,
                    timeoutCancellation.Token);
                int manualMatchScore = GetNameMatchScore(
                    NormalizeNameForComparison(data.Name),
                    databaseData.Name);
                databaseData.Notes.Insert(0, "사용자가 지정한 코토다망DB 주소로 보강했습니다.");
                if (manualMatchScore < 76)
                {
                    databaseData.Notes.Insert(1, $"GameWith 이름과 DB 이름의 일치도가 낮습니다({manualMatchScore}/100). 선택한 캐릭터가 맞는지 확인하세요.");
                }
            }
            else
            {
                databaseData = await FindDatabaseMatchAsync(
                    data.Name,
                    timeoutCancellation.Token);
            }

            if (databaseData is null)
            {
                data.Notes.Add("코토다망DB 자동 매칭에 실패했습니다. DB 개별 주소를 입력하면 등급과 그룹을 확실하게 보강할 수 있습니다.");
                return false;
            }

            MergeDatabaseData(data, databaseData);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            data.Notes.Add($"코토다망DB 자동 보강이 {Math.Ceiling(effectiveTimeout.TotalSeconds):0}초 안에 끝나지 않아 건너뛰었습니다. GameWith 정보로 계속 진행합니다.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            data.Notes.Add($"코토다망DB 자동 보강 실패: {exception.Message}");
            return false;
        }
    }

    private static List<string> ExtractSubAttributeTokens(string text, string? mainAttribute = null)
    {
        string main = DeckDataService.NormalizeAttribute(mainAttribute);
        var found = new HashSet<string>(StringComparer.Ordinal);

        void Add(string value)
        {
            string normalized = DeckDataService.NormalizeAttribute(value);
            if (normalized.Length > 0 && !string.Equals(normalized, main, StringComparison.Ordinal))
            {
                found.Add(normalized);
            }
        }

        foreach (Match match in Regex.Matches(
                     text ?? string.Empty,
                     "属性変換[【〖\\[]?(?<attr>火|水|木|光|闇|天|冥|虹)[】〗\\]]?",
                     RegexOptions.CultureInvariant))
        {
            Add(match.Groups["attr"].Value);
        }

        foreach (Match match in Regex.Matches(
                     text ?? string.Empty,
                     "サブ属性(?:文字変換)?[^。\\n]{0,50}?[（(【〖\\[](?<attr>火|水|木|光|闇|天|冥|虹)[）)】〗\\]]",
                     RegexOptions.CultureInvariant))
        {
            Add(match.Groups["attr"].Value);
        }

        foreach (Match match in Regex.Matches(
                     text ?? string.Empty,
                     "サブ属性として(?<attr>火|水|木|光|闇|天|冥|虹)属性",
                     RegexOptions.CultureInvariant))
        {
            Add(match.Groups["attr"].Value);
        }

        foreach (Match match in Regex.Matches(
                     text ?? string.Empty,
                     "(?<a>火|水|木|光|闇|天|冥|虹)[/／](?<b>火|水|木|光|闇|天|冥|虹)の2属性",
                     RegexOptions.CultureInvariant))
        {
            Add(match.Groups["a"].Value);
            Add(match.Groups["b"].Value);
        }

        return found.ToList();
    }

    public async Task<string> DownloadImageToTemporaryFileAsync(
        string imageUrl,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri) || uri is null)
        {
            return string.Empty;
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(ImageRequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.GetAsync(uri, timeoutCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"이미지 다운로드가 {ImageRequestTimeout.TotalSeconds:0}초를 초과했습니다.", exception);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeoutCancellation.Token);
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            string extension = GuessImageExtension(uri, response.Content.Headers.ContentType?.MediaType);
            string directory = Path.Combine(Path.GetTempPath(), "KotodamanWordFinder", "ImportedImages");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"character-import-{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return path;
        }
    }

    private async Task<CharacterImportData> ImportDatabaseOverrideAsync(
        string databaseUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(databaseUrl.Trim(), UriKind.Absolute, out Uri? uri) ||
            uri is null ||
            !uri.Host.EndsWith("kotodaman-db.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/character/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DB 보강용 주소는 코토다망DB의 개별 캐릭터 페이지여야 합니다.");
        }

        string html = await GetHtmlAsync(uri, cancellationToken);
        return ParseKotodamanDatabase(uri.ToString(), html);
    }

    private async Task<CharacterImportData?> FindDatabaseMatchAsync(
        string sourceName,
        CancellationToken cancellationToken)
    {
        string queryName = RemoveElementSuffix(sourceName);
        string normalizedSource = NormalizeNameForComparison(queryName);
        if (normalizedSource.Length == 0)
        {
            return null;
        }

        var candidateMap = new Dictionary<string, DatabaseSearchCandidate>(StringComparer.OrdinalIgnoreCase);
        int queryOrder = 0;
        foreach (string query in BuildDatabaseSearchQueries(queryName))
        {
            // 코토다망DB는 WordPress 검색 화면의 HTML 구조가 바뀌는 일이 있어
            // REST 검색 → 일반 검색 → post_type 검색 순서로 후보 URL을 넓게 모읍니다.
            await AddDatabaseRestSearchCandidatesAsync(
                candidateMap,
                normalizedSource,
                query,
                queryOrder,
                cancellationToken);

            string escaped = Uri.EscapeDataString(query);
            string[] searchUrls =
            {
                $"https://www.kotodaman-db.com/?s={escaped}",
                $"https://www.kotodaman-db.com/?post_type=character&s={escaped}",
                $"https://www.kotodaman-db.com/character/?s={escaped}"
            };

            foreach (string searchUrl in searchUrls)
            {
                string searchHtml;
                try
                {
                    searchHtml = await GetHtmlAsync(new Uri(searchUrl), cancellationToken);
                }
                catch
                {
                    continue;
                }

                AddDatabaseHtmlSearchCandidates(
                    candidateMap,
                    normalizedSource,
                    searchHtml,
                    queryOrder);

                if (candidateMap.Values.Count(candidate => candidate.PreviewScore >= 90) >= 2)
                {
                    break;
                }
            }

            queryOrder++;
            if (candidateMap.Values.Count(candidate => candidate.PreviewScore >= 90) >= 2)
            {
                break;
            }
        }

        DatabaseSearchCandidate[] candidates = candidateMap.Values
            .OrderByDescending(candidate => candidate.PreviewScore)
            .ThenBy(candidate => candidate.QueryOrder)
            .Take(5)
            .ToArray();

        CharacterImportData? best = null;
        int bestScore = 0;
        foreach (DatabaseSearchCandidate candidate in candidates)
        {
            try
            {
                string html = await GetHtmlAsync(new Uri(candidate.Url), cancellationToken);
                CharacterImportData parsed = ParseKotodamanDatabase(candidate.Url, html);
                int parsedScore = GetDatabasePageMatchScore(normalizedSource, html, parsed.Name);
                int score = Math.Max(candidate.PreviewScore, parsedScore);
                if (score > bestScore)
                {
                    best = parsed;
                    bestScore = score;
                }

                if (bestScore >= 100)
                {
                    break;
                }
            }
            catch
            {
                // 한 후보 페이지가 실패해도 다음 후보를 확인합니다.
            }
        }

        if (best is null || bestScore < 76)
        {
            return null;
        }

        best.Notes.Insert(0, $"코토다망DB 이름 매칭 점수 {bestScore}/100으로 자동 선택했습니다.");
        return best;
    }

    private async Task AddDatabaseRestSearchCandidatesAsync(
        Dictionary<string, DatabaseSearchCandidate> candidateMap,
        string normalizedSource,
        string query,
        int queryOrder,
        CancellationToken cancellationToken)
    {
        string escaped = Uri.EscapeDataString(query);
        string[] restUrls =
        {
            $"https://www.kotodaman-db.com/wp-json/wp/v2/search?search={escaped}&per_page=20&subtype=character",
            $"https://www.kotodaman-db.com/wp-json/wp/v2/character?search={escaped}&per_page=20"
        };

        foreach (string restUrl in restUrls)
        {
            string json;
            try
            {
                json = await GetHtmlAsync(new Uri(restUrl), cancellationToken);
            }
            catch
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement item in document.RootElement.EnumerateArray())
                {
                    string url = string.Empty;
                    string title = string.Empty;

                    if (item.TryGetProperty("url", out JsonElement urlElement) &&
                        urlElement.ValueKind == JsonValueKind.String)
                    {
                        url = urlElement.GetString() ?? string.Empty;
                    }
                    else if (item.TryGetProperty("link", out JsonElement linkElement) &&
                             linkElement.ValueKind == JsonValueKind.String)
                    {
                        url = linkElement.GetString() ?? string.Empty;
                    }

                    if (item.TryGetProperty("title", out JsonElement titleElement))
                    {
                        if (titleElement.ValueKind == JsonValueKind.String)
                        {
                            title = titleElement.GetString() ?? string.Empty;
                        }
                        else if (titleElement.ValueKind == JsonValueKind.Object &&
                                 titleElement.TryGetProperty("rendered", out JsonElement rendered) &&
                                 rendered.ValueKind == JsonValueKind.String)
                        {
                            title = rendered.GetString() ?? string.Empty;
                        }
                    }

                    string normalizedUrl = NormalizeDatabaseUrl(WebUtility.HtmlDecode(url));
                    if (normalizedUrl.Length == 0)
                    {
                        continue;
                    }

                    int previewScore = GetNameMatchScore(normalizedSource, CleanText(WebUtility.HtmlDecode(title)));
                    AddOrImproveDatabaseCandidate(
                        candidateMap,
                        new DatabaseSearchCandidate(normalizedUrl, previewScore, queryOrder));
                }

                if (candidateMap.Count > 0)
                {
                    return;
                }
            }
            catch (JsonException)
            {
                // REST가 비활성화됐거나 HTML 오류 페이지가 오면 일반 검색으로 폴백합니다.
            }
        }
    }

    private static void AddDatabaseHtmlSearchCandidates(
        Dictionary<string, DatabaseSearchCandidate> candidateMap,
        string normalizedSource,
        string searchHtml,
        int queryOrder)
    {
        foreach (Match match in CharacterLinkRegex.Matches(searchHtml))
        {
            string url = NormalizeDatabaseUrl(WebUtility.HtmlDecode(match.Groups["url"].Value));
            if (url.Length == 0)
            {
                continue;
            }

            string attributes = match.Groups["attrs"].Value;
            string linkText = CleanText(match.Groups["text"].Value);
            string title = CleanText(ExtractAttribute(attributes, "title"));
            string ariaLabel = CleanText(ExtractAttribute(attributes, "aria-label"));
            string nearbyText = ExtractNearbyCandidateText(searchHtml, match.Index, match.Length);
            int previewScore = new[] { linkText, title, ariaLabel, nearbyText }
                .Select(text => GetNameMatchScore(normalizedSource, text))
                .DefaultIfEmpty(0)
                .Max();

            AddOrImproveDatabaseCandidate(
                candidateMap,
                new DatabaseSearchCandidate(url, previewScore, queryOrder));
        }

        // 링크 태그 구조가 바뀌어도 /character/1234/ URL 자체만 있으면 후보로 살립니다.
        foreach (Match match in CharacterUrlRegex.Matches(searchHtml))
        {
            string url = NormalizeDatabaseUrl(WebUtility.HtmlDecode(match.Groups["url"].Value));
            if (url.Length == 0)
            {
                continue;
            }

            string nearbyText = ExtractNearbyCandidateText(searchHtml, match.Index, match.Length);
            int previewScore = GetNameMatchScore(normalizedSource, nearbyText);
            AddOrImproveDatabaseCandidate(
                candidateMap,
                new DatabaseSearchCandidate(url, previewScore, queryOrder));
        }
    }

    private static void AddOrImproveDatabaseCandidate(
        Dictionary<string, DatabaseSearchCandidate> candidateMap,
        DatabaseSearchCandidate candidate)
    {
        if (!candidateMap.TryGetValue(candidate.Url, out DatabaseSearchCandidate? existing) ||
            candidate.PreviewScore > existing.PreviewScore ||
            (candidate.PreviewScore == existing.PreviewScore && candidate.QueryOrder < existing.QueryOrder))
        {
            candidateMap[candidate.Url] = candidate;
        }
    }

    private static IEnumerable<string> BuildDatabaseSearchQueries(string sourceName)
    {
        string normalized = sourceName.Normalize(NormalizationForm.FormKC).Trim();
        var queries = new List<string>
        {
            normalized,
            RemoveParentheticalText(normalized),
            RemoveEpithet(normalized),
            RemoveParentheticalText(RemoveEpithet(normalized))
        };

        int separator = normalized.LastIndexOf('・');
        if (separator >= 0 && separator < normalized.Length - 1)
        {
            queries.Add(normalized[(separator + 1)..]);
        }

        string symbolReduced = Regex.Replace(
            normalized,
            "[\\s・･·「」『』〖〗【】［］\\[\\]（）()]+",
            string.Empty);
        queries.Add(symbolReduced);

        return queries
            .Select(query => query.Trim())
            .Where(query => query.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3);
    }

    private static int GetDatabasePageMatchScore(
        string normalizedSource,
        string html,
        string parsedName)
    {
        int score = GetNameMatchScore(normalizedSource, parsedName);
        foreach (string alias in ExtractDatabaseAliases(html))
        {
            score = Math.Max(score, GetNameMatchScore(normalizedSource, alias));
        }
        return score;
    }

    private static IEnumerable<string> ExtractDatabaseAliases(string html)
    {
        var aliases = new List<string>();
        string heading = ExtractFirstHeading(html);
        if (heading.Length > 0)
        {
            aliases.Add(heading);
            aliases.Add(RemoveEpithet(heading));
        }

        List<string> lines = HtmlToLines(html);
        foreach (string label in new[] { "進化前の名前", "絵違いの名前", "モードシフト前の名前", "モードシフト後の名前" })
        {
            int index = FindLastExactLine(lines, label);
            if (index < 0)
            {
                continue;
            }

            for (int cursor = index + 1; cursor < lines.Count && cursor < index + 4; cursor++)
            {
                string value = lines[cursor].Trim();
                if (value.Length == 0 || value == "-" || value == "なし")
                {
                    continue;
                }
                aliases.Add(value);
                aliases.AddRange(value.Split(new[] { '、', ',', '，' }, StringSplitOptions.RemoveEmptyEntries));
                break;
            }
        }

        return aliases
            .Select(alias => alias.Trim())
            .Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractNearbyCandidateText(string html, int linkIndex, int linkLength)
    {
        int start = Math.Max(0, linkIndex - 220);
        int end = Math.Min(html.Length, linkIndex + linkLength + 420);
        return CleanText(html[start..end]);
    }

    private sealed record DatabaseSearchCandidate(string Url, int PreviewScore, int QueryOrder);

    private static (string Attribute, string Species) ExtractGameWithBasicInfoMetadata(
        string? html,
        string? basicInfoText)
    {
        // nullable 흐름 분석과 실제 파서 모두에서 non-null 문자열만 다루도록
        // 입구에서 명시적으로 고정합니다. (CS8604 방지)
        string sourceHtml = html ?? string.Empty;
        string sourceBasicInfoText = basicInfoText ?? string.Empty;

        // GameWith의 "性能について" 문장에는 킬러 대상의 속성/종족도 등장합니다.
        // 따라서 텍스트 전체를 먼저 훑지 않고, 반드시 "レア度 / 属性 / 種族" 기본정보 표를 우선합니다.
        string attribute = string.Empty;
        string species = string.Empty;

        foreach (Match tableMatch in HtmlTableRegex.Matches(sourceHtml))
        {
            string tableHtml = tableMatch.Value;
            string tableText = CleanText(tableHtml);
            if (!tableText.Contains("属性", StringComparison.Ordinal) ||
                !tableText.Contains("種族", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match cellMatch in HtmlCellRegex.Matches(tableHtml))
            {
                string cellHtml = cellMatch.Groups["cell"].Value;
                if (attribute.Length == 0)
                {
                    attribute = ExtractGameWithAttributeFromFragment(cellHtml);
                }
                if (species.Length == 0)
                {
                    species = ExtractGameWithSpeciesFromFragment(cellHtml);
                }

                if (attribute.Length > 0 && species.Length > 0)
                {
                    return (attribute, species);
                }
            }
        }

        // 오래된 페이지나 표 구조가 약간 다른 경우를 위한 제한적 보조 경로입니다.
        // 기본정보 표에서 못 찾았을 때만 기본정보 텍스트를 확인합니다.
        if (attribute.Length == 0)
        {
            attribute = ExtractAttributeToken(sourceBasicInfoText);
        }
        if (species.Length == 0)
        {
            species = ExtractSpeciesToken(sourceBasicInfoText);
        }

        // 전체 페이지의 특성/킬러 문구를 읽으면 적 속성·적 종족을 잘못 잡을 수 있으므로
        // 그 뒤에도 라벨 인접 영역만 보조로 사용합니다.
        if (attribute.Length == 0)
        {
            attribute = ExtractDatabaseIconTokenNearLabel(
                sourceHtml,
                "属性",
                new[] { "火", "水", "木", "光", "闇", "天", "冥", "虹" },
                "属性");
        }
        if (species.Length == 0)
        {
            species = ExtractGameWithSpeciesNearLabel(sourceHtml);
        }

        return (attribute, species);
    }

    private static string ExtractGameWithAttributeFromFragment(string htmlFragment)
    {
        string textValue = CleanText(htmlFragment ?? string.Empty);
        string attribute = ExtractAttributeToken(textValue);
        if (attribute.Length > 0)
        {
            return attribute;
        }

        foreach (Match tagMatch in Regex.Matches(
                     htmlFragment ?? string.Empty,
                     @"<(?:img|span|div|a)\b(?<attrs>[^>]*)>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string attrs = tagMatch.Groups["attrs"].Value;
            foreach (string attributeName in new[] { "alt", "title", "aria-label", "data-name" })
            {
                string raw = CleanText(ExtractAttribute(attrs, attributeName));
                attribute = ExtractAttributeToken(raw);
                if (attribute.Length > 0)
                {
                    return attribute;
                }

                attribute = DeckDataService.NormalizeAttribute(raw);
                if (attribute.Length > 0)
                {
                    return attribute;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractGameWithSpeciesFromFragment(string htmlFragment)
    {
        string textValue = CleanText(htmlFragment ?? string.Empty);
        string species = ExtractSpeciesToken(textValue);
        if (species.Length > 0)
        {
            return species;
        }

        foreach (Match tagMatch in Regex.Matches(
                     htmlFragment ?? string.Empty,
                     @"<(?:img|span|div|a)\b(?<attrs>[^>]*)>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string attrs = tagMatch.Groups["attrs"].Value;
            foreach (string attributeName in new[] { "alt", "title", "aria-label", "data-name" })
            {
                string raw = CleanText(ExtractAttribute(attrs, attributeName));
                species = ExtractSpeciesToken(raw);
                if (species.Length == 0)
                {
                    species = NormalizeGameWithSpeciesName(raw);
                }
                if (species.Length > 0)
                {
                    return species;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractGameWithSpeciesNearLabel(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        int labelIndex = html.IndexOf("種族", StringComparison.Ordinal);
        if (labelIndex < 0)
        {
            return string.Empty;
        }

        int end = Math.Min(html.Length, labelIndex + 1800);
        return ExtractGameWithSpeciesFromFragment(html[labelIndex..end]);
    }

    private static string ExtractGameWithRarity(string html, string basicInfoText)
    {
        string source = (basicInfoText ?? string.Empty).Normalize(NormalizationForm.FormKC);
        Match match = Regex.Match(
            source,
            @"レアリティ\s*[:：|]?\s*(?:星|★)?(?<rarity>[1-6])|(?:星|★)(?<rarity>[1-6])",
            RegexOptions.CultureInvariant);
        if (match.Success)
        {
            return match.Groups["rarity"].Value;
        }

        // 사이드바의 다른 캐릭터 설명에 있는 '星6'을 잘못 읽지 않도록
        // 레어리티 라벨 주변의 짧은 HTML 조각만 보조로 확인합니다.
        int labelIndex = html.IndexOf("レアリティ", StringComparison.Ordinal);
        if (labelIndex < 0)
        {
            return string.Empty;
        }

        int start = Math.Max(0, labelIndex - 300);
        int length = Math.Min(1800, html.Length - start);
        string fragment = CleanText(html.Substring(start, length))
            .Normalize(NormalizationForm.FormKC);
        match = Regex.Match(
            fragment,
            @"レアリティ\s*[:：|]?\s*(?:星|★)?(?<rarity>[1-6])",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["rarity"].Value : string.Empty;
    }

    private static CharacterImportData ParseGameWith(
        string sourceUrl,
        string html,
        IReadOnlyCollection<string>? knownGroups,
        IReadOnlyDictionary<string, string[]>? knownGroupRelations)
    {
        List<string> lines = HtmlToLines(html);
        string name = ExtractGameWithCharacterName(html, lines);

        bool hasModeShift = lines.Any(line => string.Equals(line, "モード・シフト", StringComparison.Ordinal));
        if (hasModeShift)
        {
            name = RemoveElementSuffix(name);
        }

        var letters = new HashSet<string>(StringComparer.Ordinal);
        int letterHeadingIndex = lines.FindIndex(line => string.Equals(line, "文字の使いやすさ", StringComparison.Ordinal));
        if (letterHeadingIndex >= 0)
        {
            for (int index = letterHeadingIndex + 1; index < lines.Count && index < letterHeadingIndex + 40; index++)
            {
                string line = lines[index];
                if (line.StartsWith("※", StringComparison.Ordinal) ||
                    string.Equals(line, "ステータス", StringComparison.Ordinal))
                {
                    break;
                }

                AddKanaListLine(letters, line);
            }
        }

        string plainText = string.Join("\n", lines);
        foreach (Match match in Regex.Matches(
                     plainText,
                     "文字変換(?:に)?[「\\\"](?<letters>[^」\\\"]+)[」\\\"]",
                     RegexOptions.CultureInvariant))
        {
            AddKanaTokens(letters, match.Groups["letters"].Value);
        }

        string imageUrl = FindGameWithCharacterImageUrl(html, name);
        string basicInfoText = ExtractMetadataSectionText(lines);
        string rarity = ExtractGameWithRarity(html, basicInfoText);
        (string attribute, string species) = ExtractGameWithBasicInfoMetadata(html, basicInfoText);

        List<string> subAttributes = ExtractSubAttributeTokens(plainText, attribute);
        var data = new CharacterImportData
        {
            Name = name,
            Category = CharacterCategories.Other,
            Rarity = rarity,
            Attribute = attribute,
            SubAttributes = subAttributes,
            Species = species,
            Letters = NormalizeLetters(letters),
            ImageUrl = imageUrl,
            SourceUrl = sourceUrl,
            SourceSite = "GameWith"
        };

        InferGameWithGroups(data, lines, knownGroups, knownGroupRelations);
        data.IsCollaboration = InferGameWithCollaborationStatus(html, data);

        if (name.Length == 0)
        {
            data.Notes.Add("GameWith 페이지에서 캐릭터 이름을 자동 인식하지 못했습니다.");
        }
        if (data.Letters.Count == 0)
        {
            data.Notes.Add("GameWith 페이지에서 사용 문자를 자동 인식하지 못했습니다.");
        }
        if (hasModeShift)
        {
            data.Notes.Add("모드시프트 캐릭터로 보입니다. 가져온 뒤 동일 이름 형태 또는 이름 다른 모드시프트 연결을 확인하세요.");
        }

        return data;
    }

    private static void InferGameWithGroups(
        CharacterImportData data,
        IReadOnlyList<string> lines,
        IReadOnlyCollection<string>? knownGroups,
        IReadOnlyDictionary<string, string[]>? knownGroupRelations)
    {
        string plainText = string.Join("\n", lines);
        string traitText = ExtractGameWithTraitText(lines);
        var known = (knownGroups ?? Array.Empty<string>())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var relationMap = BuildGroupRelationMap(knownGroupRelations);
        var includedGroups = ExtractExplicitGameWithIncludedGroups(plainText, known);
        data.IncludedGroups = includedGroups
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void AddScore(string group, int score)
        {
            string normalized = group.Trim();
            if (normalized.Length == 0 || LooksLikeNonGroupLabel(normalized))
            {
                return;
            }

            if (!scores.TryGetValue(normalized, out int current) || score > current)
            {
                scores[normalized] = score;
            }
        }

        // 상위 그룹의 포함 규칙과 GameWith의 명시적 포함 문장이 일치하면 가장 확실한 소속으로 봅니다.
        foreach ((string parent, string[] children) in relationMap)
        {
            if (children.Length == 0)
            {
                continue;
            }

            int matchedChildren = children.Count(child =>
                includedGroups.Contains(child, StringComparer.OrdinalIgnoreCase));
            if (matchedChildren == children.Length)
            {
                AddScore(parent, 150 + Math.Min(children.Length, 20));
            }
            else if (matchedChildren >= 2 && matchedChildren * 2 >= children.Length)
            {
                AddScore(parent, 100 + matchedChildren);
            }
        }

        // "다른 그룹으로도 취급"되는 그룹은 실제 소속 그룹과 다를 수 있으므로
        // 포함 그룹이 하나뿐이라는 이유만으로 실제 소속으로 확정하지 않습니다.

        // "자신 이외의 ○○가 덱에 있는 경우"는 해당 캐릭터가 그 그룹에 속한다는 강한 단서입니다.
        foreach (Match match in Regex.Matches(
                     traitText,
                     @"デッキ内に自身以外の(?<groups>(?:「[^」]+」\s*(?:または|・|、)?\s*)+)",
                     RegexOptions.CultureInvariant))
        {
            foreach (string group in ExtractQuotedGroups(match.Groups["groups"].Value, known))
            {
                AddScore(group, relationMap.ContainsKey(group) ? 96 : 86);
            }
        }

        // 그룹 전용 내성은 일반적으로 자기 소속 그룹에 붙는 특성입니다.
        foreach (Match match in Regex.Matches(
                     traitText,
                     @"「(?<group>[^」]+)」[^\n]{0,28}?(?:炎上|毒|睡眠|混乱|呪い|衰弱)耐性",
                     RegexOptions.CultureInvariant))
        {
            foreach (string group in ExpandGroupPhrase(match.Groups["group"].Value, known))
            {
                AddScore(group, 105);
            }
        }

        // 특성 제목/본문에 이미 등록된 그룹명이 직접 등장하는 경우를 보조 단서로 사용합니다.
        foreach (string group in known)
        {
            int count = CountOccurrences(traitText, group);
            if (count <= 0)
            {
                continue;
            }

            int score = count >= 3 ? 82 : count == 2 ? 72 : 58;
            if (relationMap.ContainsKey(group) && includedGroups.Count > 0)
            {
                score += 12;
            }
            AddScore(group, score);
        }

        // 명시적 "그룹으로 취급" 문장에서 상위 그룹을 찾지 못했을 때는 포함 그룹들을 후보로 남깁니다.
        foreach (string group in includedGroups)
        {
            AddScore(group, 55);
        }

        var ranked = scores
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();

        data.GroupCandidates = ranked
            .Select(item => item.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (ranked.Length > 0)
        {
            int topScore = ranked[0].Value;
            int secondScore = ranked.Length > 1 ? ranked[1].Value : 0;
            bool isConfident = topScore >= 100 || (topScore >= 88 && topScore - secondScore >= 12);
            if (isConfident)
            {
                data.GroupName = ranked[0].Key;
            }
        }

        if (data.GroupName.Length > 0)
        {
            data.IncludedGroups = data.IncludedGroups
                .Where(group => !string.Equals(group, data.GroupName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            data.Notes.Add($"GameWith 특성에서 소속 그룹을 추정했습니다: {data.GroupName}");
        }
        else if (data.GroupCandidates.Count > 0)
        {
            data.Notes.Add($"GameWith 특성에서 그룹 후보를 찾았습니다. 미리보기에서 확인하세요: {string.Join(" · ", data.GroupCandidates.Take(4))}");
        }
        else
        {
            data.Notes.Add("GameWith 특성에서 소속 그룹을 확정하지 못했습니다. 필요한 경우 직접 선택하세요.");
        }

        if (data.IncludedGroups.Count > 0)
        {
            data.Notes.Add($"같이 취급되는 그룹을 인식했습니다: {string.Join(" · ", data.IncludedGroups)}");
        }
    }

    private static string ExtractGameWithTraitText(IReadOnlyList<string> lines)
    {
        int start = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            string line = lines[index];
            if (line.EndsWith("の特性", StringComparison.Ordinal) &&
                !line.Contains("リーダー", StringComparison.Ordinal) &&
                !line.Contains("祝福", StringComparison.Ordinal))
            {
                start = index;
                break;
            }
        }

        if (start < 0)
        {
            start = lines.ToList().FindIndex(line =>
                string.Equals(line, "特性/リーダー特性/祝福特性", StringComparison.Ordinal));
        }

        if (start < 0)
        {
            return string.Join("\n", lines);
        }

        int end = lines.Count;
        for (int index = start + 1; index < lines.Count; index++)
        {
            string line = lines[index];
            if (line.EndsWith("のEXスキル", StringComparison.Ordinal) ||
                line.EndsWith("のチャージスキル", StringComparison.Ordinal) ||
                line.EndsWith("の祝福特性", StringComparison.Ordinal) ||
                line.EndsWith("の入手方法", StringComparison.Ordinal))
            {
                end = index;
                break;
            }
        }

        return string.Join("\n", lines.Skip(start).Take(Math.Max(0, end - start)));
    }

    private static List<string> ExtractExplicitGameWithIncludedGroups(
        string plainText,
        IReadOnlyCollection<string> knownGroups)
    {
        var groups = new List<string>();
        foreach (Match match in Regex.Matches(
                     plainText,
                     @"このコトダマンは(?<body>.{0,320}?)のグループに属しているものとして扱われる",
                     RegexOptions.CultureInvariant | RegexOptions.Singleline))
        {
            groups.AddRange(ExtractQuotedGroups(match.Groups["body"].Value, knownGroups));
        }

        return groups
            .Where(group => !LooksLikeNonGroupLabel(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ExtractQuotedGroups(
        string text,
        IReadOnlyCollection<string> knownGroups)
    {
        foreach (Match quote in Regex.Matches(text, "「(?<group>[^」]+)」", RegexOptions.CultureInvariant))
        {
            foreach (string group in ExpandGroupPhrase(quote.Groups["group"].Value, knownGroups))
            {
                yield return group;
            }
        }
    }

    private static IEnumerable<string> ExpandGroupPhrase(
        string value,
        IReadOnlyCollection<string> knownGroups)
    {
        string cleaned = value.Normalize(NormalizationForm.FormKC).Trim();
        if (cleaned.Length == 0)
        {
            yield break;
        }

        string? exactKnown = knownGroups.FirstOrDefault(group =>
            string.Equals(group, cleaned, StringComparison.OrdinalIgnoreCase));
        if (exactKnown is not null)
        {
            yield return exactKnown;
            yield break;
        }

        foreach (string alternative in cleaned.Split(
                     new[] { "または" },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string item = alternative.Trim();
            if (item.EndsWith("の戦律", StringComparison.Ordinal) && item.Contains('・'))
            {
                string prefix = item[..^"の戦律".Length];
                foreach (string token in prefix.Split('・', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return token + "の戦律";
                }
                continue;
            }

            string[] tokens = item.Split(
                new[] { '・', '、', ',', '，', '/' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 1 && tokens.All(token => knownGroups.Any(group =>
                    string.Equals(group, token, StringComparison.OrdinalIgnoreCase))))
            {
                foreach (string token in tokens)
                {
                    string canonical = knownGroups.First(group =>
                        string.Equals(group, token, StringComparison.OrdinalIgnoreCase));
                    yield return canonical;
                }
                continue;
            }

            yield return item;
        }
    }

    private static Dictionary<string, string[]> BuildGroupRelationMap(
        IReadOnlyDictionary<string, string[]>? knownGroupRelations)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (knownGroupRelations is not null)
        {
            foreach ((string parent, string[] children) in knownGroupRelations)
            {
                if (string.IsNullOrWhiteSpace(parent))
                {
                    continue;
                }

                result[parent.Trim()] = (children ?? Array.Empty<string>())
                    .Where(group => !string.IsNullOrWhiteSpace(group))
                    .Select(group => group.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        AddDefaultRelation(result, "全の戦律", new[]
        {
            "斬の戦律", "砲の戦律", "突の戦律", "重の戦律", "超の戦律", "打の戦律"
        });
        AddDefaultRelation(result, "三国の願い", new[]
        {
            "セイユニマ", "ブリタンディ", "リザンテクス"
        });
        AddDefaultRelation(result, "『夢』への旅路", new[]
        {
            "セイユニマ", "ブリタンディ", "リザンテクス", "廻る魂", "此方へ", "月の夢"
        });
        return result;
    }

    private static void AddDefaultRelation(
        IDictionary<string, string[]> relations,
        string parent,
        string[] children)
    {
        if (!relations.TryGetValue(parent, out string[]? existing) || existing.Length == 0)
        {
            relations[parent] = children;
        }
    }

    private static bool LooksLikeNonGroupLabel(string value)
        => value.Contains("援護(", StringComparison.Ordinal) ||
           value.Contains("テーマ", StringComparison.Ordinal) ||
           value.Contains("種族", StringComparison.Ordinal) ||
           value.Contains("属性", StringComparison.Ordinal) ||
           value.Length > 48;

    private static int CountOccurrences(string text, string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static CharacterImportData ParseKotodamanDatabase(string sourceUrl, string html)
    {
        List<string> lines = HtmlToLines(html);
        string fullName = ExtractFirstHeading(html);
        if (fullName.Length == 0)
        {
            fullName = lines.FirstOrDefault(line => line.Length > 0) ?? string.Empty;
        }

        string shortName = RemoveEpithet(fullName);
        string rarity = FindLabelValue(lines, "レアリティ", new[] { "なし", "スペシャル", "レジェンド", "グランド", "ドリーム", "ミラクル", "6", "5", "4", "3", "2", "1" });
        string groupName = FindNextValue(lines, "所属", IsReasonableGroupValue);
        string metadataText = ExtractMetadataSectionText(lines);
        string attribute = FindLabelValue(lines, "属性", new[] { "火", "水", "木", "光", "闇", "天", "冥", "虹" });
        if (attribute.Length == 0)
        {
            attribute = ExtractDatabaseIconTokenNearLabel(
                html,
                "属性",
                new[] { "火", "水", "木", "光", "闇", "天", "冥", "虹" },
                "属性");
        }
        if (attribute.Length == 0)
        {
            attribute = ExtractAttributeToken(metadataText);
        }

        string species = FindLabelValue(lines, "種族", new[] { "神", "魔", "英", "龍", "獣", "霊", "物", "妖" });
        if (species.Length == 0)
        {
            species = ExtractDatabaseIconTokenNearLabel(
                html,
                "種族",
                new[] { "神", "魔", "英", "龍", "獣", "霊", "物", "妖" },
                "種族");
        }
        if (species.Length == 0)
        {
            species = ExtractSpeciesToken(metadataText);
        }

        var letters = new HashSet<string>(StringComparer.Ordinal);
        int lettersIndex = lines.FindLastIndex(line => string.Equals(line, "文字", StringComparison.Ordinal));
        if (lettersIndex >= 0)
        {
            for (int index = lettersIndex + 1; index < lines.Count && index < lettersIndex + 8; index++)
            {
                string line = lines[index];
                if (line.StartsWith("入手方法", StringComparison.Ordinal) ||
                    string.Equals(line, "実装日", StringComparison.Ordinal))
                {
                    break;
                }
                AddKanaTokens(letters, line);
            }
        }

        string plainText = string.Join("\n", lines);
        foreach (Match match in Regex.Matches(
                     plainText,
                     "追加文字[：:]?(?<letters>[^\\n]+)",
                     RegexOptions.CultureInvariant))
        {
            AddKanaTokens(letters, match.Groups["letters"].Value);
        }

        var includedGroups = new List<string>();
        foreach (Match match in Regex.Matches(
                     plainText,
                     "このコトダマンは「(?<groups>[^」]+)」のグループに属しているものとして扱われる",
                     RegexOptions.CultureInvariant))
        {
            includedGroups.AddRange(SplitGroupNames(match.Groups["groups"].Value));
        }

        var data = new CharacterImportData
        {
            Name = shortName,
            Category = MapDatabaseCategory(rarity),
            Rarity = rarity,
            Attribute = attribute,
            Species = species,
            GroupName = groupName,
            IncludedGroups = includedGroups
                .Where(group => !string.Equals(group, groupName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Letters = NormalizeLetters(letters),
            ImageUrl = FindPreferredImageUrl(html, shortName, preferGameWith: false),
            SourceUrl = sourceUrl,
            SourceSite = "코토다망DB",
            MatchedDatabaseUrl = sourceUrl
        };

        if (data.Category == CharacterCategories.Other)
        {
            data.Notes.Add("DB 레어리티가 일반 또는 미분류입니다. 앱에서는 '기타'로 가져왔습니다.");
        }
        if (data.Letters.Count == 0)
        {
            data.Notes.Add("DB 페이지에서 사용 문자를 자동 인식하지 못했습니다.");
        }
        if (data.Attribute.Length == 0 || data.Species.Length == 0)
        {
            data.Notes.Add("DB 페이지에서 속성/종족 일부를 자동 인식하지 못했습니다.");
        }

        return data;
    }

    private static void MergeDatabaseData(CharacterImportData target, CharacterImportData database)
    {
        if (string.IsNullOrWhiteSpace(target.Rarity))
        {
            target.Rarity = database.Rarity;
        }
        if (target.Category == CharacterCategories.Other && database.Category != CharacterCategories.Other)
        {
            target.Category = database.Category;
        }
        if (string.IsNullOrWhiteSpace(target.Attribute))
        {
            target.Attribute = database.Attribute;
        }
        target.SubAttributes = DeckDataService.NormalizeAttributes(
            target.SubAttributes.Concat(database.SubAttributes),
            string.IsNullOrWhiteSpace(target.Attribute) ? database.Attribute : target.Attribute);
        if (string.IsNullOrWhiteSpace(target.Species))
        {
            target.Species = database.Species;
        }
        if (string.IsNullOrWhiteSpace(target.GroupName))
        {
            target.GroupName = database.GroupName;
        }
        target.IncludedGroups = database.IncludedGroups.ToList();
        target.Letters = NormalizeLetters(target.Letters.Concat(database.Letters));
        target.MatchedDatabaseUrl = database.SourceUrl;
        target.Notes.Add($"코토다망DB 일치 항목으로 등급·속성·종족·그룹·문자를 보강했습니다: {database.Name}");
        target.Notes.AddRange(database.Notes);
    }

    private static string ExtractDatabaseIconTokenNearLabel(
        string html,
        string label,
        IReadOnlyCollection<string> allowedValues,
        string optionalSuffix)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        int labelIndex = html.IndexOf(label, StringComparison.Ordinal);
        if (labelIndex < 0)
        {
            return string.Empty;
        }

        int end = Math.Min(html.Length, labelIndex + 1800);
        string fragment = html[labelIndex..end];
        foreach (Match tagMatch in Regex.Matches(
                     fragment,
                     @"<(?:img|span|div|td|dd|li)\b(?<attrs>[^>]*)>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string attrs = tagMatch.Groups["attrs"].Value;
            foreach (string attributeName in new[] { "alt", "title", "aria-label", "data-name" })
            {
                string raw = CleanText(ExtractAttribute(attrs, attributeName))
                    .Normalize(NormalizationForm.FormKC)
                    .Trim();
                if (raw.Length == 0)
                {
                    continue;
                }

                foreach (string allowed in allowedValues)
                {
                    if (string.Equals(raw, allowed, StringComparison.Ordinal) ||
                        string.Equals(raw, allowed + optionalSuffix, StringComparison.Ordinal))
                    {
                        return allowed;
                    }
                }
            }
        }

        string plain = CleanText(fragment);
        foreach (string allowed in allowedValues)
        {
            if (Regex.IsMatch(
                    plain,
                    $@"{Regex.Escape(label)}\s*[:：|]?\s*{Regex.Escape(allowed)}(?:{Regex.Escape(optionalSuffix)})?",
                    RegexOptions.CultureInvariant))
            {
                return allowed;
            }
        }

        return string.Empty;
    }

    private static string ExtractMetadataSectionText(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        int start = 0;
        for (int index = 0; index < lines.Count; index++)
        {
            if (string.Equals(lines[index], "基本情報", StringComparison.Ordinal))
            {
                start = index;
                break;
            }
        }

        int end = Math.Min(lines.Count, start + 80);
        for (int index = start + 1; index < end; index++)
        {
            if (string.Equals(lines[index], "文字", StringComparison.Ordinal) ||
                string.Equals(lines[index], "文字の使いやすさ", StringComparison.Ordinal) ||
                string.Equals(lines[index], "ステータス", StringComparison.Ordinal))
            {
                end = index;
                break;
            }
        }

        return string.Join(" ", lines.Skip(start).Take(Math.Max(0, end - start)));
    }

    private static string ExtractAttributeToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        Match explicitMatch = Regex.Match(
            text,
            @"(?<value>火|水|木|光|闇|天|冥|虹)\s*属性",
            RegexOptions.CultureInvariant);
        if (explicitMatch.Success)
        {
            return explicitMatch.Groups["value"].Value;
        }

        Match labelMatch = Regex.Match(
            text,
            @"属性\s*[:：|]?\s*(?<value>火|水|木|光|闇|天|冥|虹)(?![属性])",
            RegexOptions.CultureInvariant);
        return labelMatch.Success ? labelMatch.Groups["value"].Value : string.Empty;
    }

    private static string ExtractSpeciesToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Normalize(NormalizationForm.FormKC);
        Match explicitMatch = Regex.Match(
            normalized,
            @"(?<value>英雄|悪魔|神|魔|英|龍|竜|獣|霊|物|妖)\s*種族",
            RegexOptions.CultureInvariant);
        if (explicitMatch.Success)
        {
            return NormalizeGameWithSpeciesName(explicitMatch.Groups["value"].Value);
        }

        Match labelMatch = Regex.Match(
            normalized,
            @"種族\s*[:：|]?\s*(?<value>英雄|悪魔|神|魔|英|龍|竜|獣|霊|物|妖)(?![種族])",
            RegexOptions.CultureInvariant);
        return labelMatch.Success
            ? NormalizeGameWithSpeciesName(labelMatch.Groups["value"].Value)
            : string.Empty;
    }

    private static string NormalizeGameWithSpeciesName(string value)
    {
        string normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        if (normalized.EndsWith("種族", StringComparison.Ordinal))
        {
            normalized = normalized[..^2].Trim();
        }

        return normalized switch
        {
            "英雄" => "英",
            "悪魔" => "魔",
            "竜" => "龍",
            "神" or "魔" or "英" or "龍" or "獣" or "霊" or "物" or "妖" => normalized,
            _ => string.Empty
        };
    }

    private async Task<string> GetHtmlAsync(Uri uri, CancellationToken cancellationToken)
    {
        bool isDatabaseRequest = uri.Host.EndsWith("kotodaman-db.com", StringComparison.OrdinalIgnoreCase);
        int maxAttempts = isDatabaseRequest ? 2 : 3;
        TimeSpan requestTimeout = isDatabaseRequest ? DatabaseRequestTimeout : GameWithRequestTimeout;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(requestTimeout);

            try
            {
                using HttpResponseMessage response = await HttpClient.GetAsync(uri, requestCancellation.Token);
                if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504)
                {
                    if (attempt < maxAttempts)
                    {
                        int retryDelay = response.Headers.RetryAfter?.Delta is TimeSpan delta
                            ? (int)Math.Clamp(delta.TotalMilliseconds, 500, 4000)
                            : 650 * attempt;
                        await Task.Delay(retryDelay, cancellationToken);
                        continue;
                    }
                }

                response.EnsureSuccessStatusCode();
                byte[] bytes = await response.Content.ReadAsByteArrayAsync(requestCancellation.Token);
                if (bytes.Length == 0)
                {
                    throw new HttpRequestException("빈 응답을 받았습니다.");
                }

                string? charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                Encoding encoding = Encoding.UTF8;
                if (!string.IsNullOrWhiteSpace(charset))
                {
                    try
                    {
                        encoding = Encoding.GetEncoding(charset);
                    }
                    catch
                    {
                        encoding = Encoding.UTF8;
                    }
                }
                return encoding.GetString(bytes);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"요청 시간이 {requestTimeout.TotalSeconds:0}초를 초과했습니다.",
                    exception);
            }
            catch (HttpRequestException exception)
            {
                lastException = exception;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(650 * attempt, cancellationToken);
            }
        }

        throw new HttpRequestException($"페이지를 읽지 못했습니다: {uri}", lastException);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) KotodamanWordFinder/{AppPaths.AppVersion}");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ja-JP,ja;q=0.9,ko-KR;q=0.8");
        return client;
    }

    private static List<string> HtmlToLines(string html)
    {
        string withBreaks = Regex.Replace(
            html,
            "</?(?:br|p|div|li|tr|td|th|h[1-6]|dt|dd|section|article)[^>]*>",
            "\n",
            RegexOptions.IgnoreCase);
        string withoutScripts = Regex.Replace(
            withBreaks,
            "<(script|style)[^>]*>.*?</\\1>",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        string withoutTags = Regex.Replace(withoutScripts, "<[^>]+>", string.Empty);
        string decoded = WebUtility.HtmlDecode(withoutTags).Normalize(NormalizationForm.FormKC);
        return decoded
            .Replace("\r", string.Empty)
            .Split('\n')
            .Select(line => Regex.Replace(line, "\\s+", " ").Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static string ExtractFirstHeading(string html)
    {
        Match match = Regex.Match(html, "<h1[^>]*>(?<value>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? CleanText(match.Groups["value"].Value) : string.Empty;
    }

    private static string ExtractGameWithCharacterName(string html, IReadOnlyList<string> lines)
    {
        var candidates = new List<string>
        {
            ExtractFirstHeading(html),
            ExtractMetaContent(html, "og:title"),
            ExtractHtmlTitle(html)
        };

        candidates.AddRange(lines.Where(line =>
            line.Contains("の評価とステータス", StringComparison.Ordinal) ||
            line.Contains("の評価とキャラ情報", StringComparison.Ordinal)));

        foreach (string candidate in candidates)
        {
            string name = CleanGameWithCharacterName(candidate);
            if (name.Length > 0 &&
                !string.Equals(name, "全キャラ一覧", StringComparison.Ordinal) &&
                !name.Contains("ランキング", StringComparison.Ordinal))
            {
                return name;
            }
        }

        return string.Empty;
    }

    private static string CleanGameWithCharacterName(string value)
    {
        string cleaned = CleanText(value).Normalize(NormalizationForm.FormKC).Trim();
        cleaned = Regex.Replace(
            cleaned,
            @"^[【〖\[]\s*コトダマン\s*[】〗\]]\s*",
            string.Empty,
            RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(
            cleaned,
            @"\s*[-｜|]\s*ゲームウィズ.*$",
            string.Empty,
            RegexOptions.CultureInvariant);

        int suffixIndex = cleaned.IndexOf("の評価", StringComparison.Ordinal);
        if (suffixIndex > 0)
        {
            cleaned = cleaned[..suffixIndex];
        }

        return cleaned.Trim(' ', '-', '｜', '|');
    }

    private static string ExtractMetaContent(string html, string propertyName)
    {
        Match propertyFirst = Regex.Match(
            html,
            $"<meta[^>]+(?:property|name)=[\"']{Regex.Escape(propertyName)}[\"'][^>]+content=[\"'](?<value>[^\"']+)[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (propertyFirst.Success)
        {
            return WebUtility.HtmlDecode(propertyFirst.Groups["value"].Value).Trim();
        }

        Match contentFirst = Regex.Match(
            html,
            $"<meta[^>]+content=[\"'](?<value>[^\"']+)[\"'][^>]+(?:property|name)=[\"']{Regex.Escape(propertyName)}[\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return contentFirst.Success
            ? WebUtility.HtmlDecode(contentFirst.Groups["value"].Value).Trim()
            : string.Empty;
    }

    private static string ExtractHtmlTitle(string html)
    {
        Match match = Regex.Match(
            html,
            "<title[^>]*>(?<value>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? CleanText(match.Groups["value"].Value) : string.Empty;
    }

    private static string FindGameWithCharacterImageUrl(string html, string name)
    {
        string basicInfoSection = ExtractGameWithBasicInfoSection(html);
        if (basicInfoSection.Length > 0)
        {
            // 기본정보 영역에서 못 찾았다면 페이지 전체의 광고/속성 아이콘을 억지로 고르지 않습니다.
            // 이미지가 비는 편이 잘못된 이미지를 자동 저장하는 것보다 안전합니다.
            return FindPreferredImageUrl(
                basicInfoSection,
                name,
                preferGameWith: true,
                isGameWithBasicInfoSection: true);
        }

        return FindPreferredImageUrl(html, name, preferGameWith: true);
    }

    private static string ExtractGameWithBasicInfoSection(string html)
    {
        Match heading = Regex.Match(
            html,
            @"<h[2-4]\b[^>]*>[^<]*(?:<[^>]+>[^<]*)*基本情報.*?</h[2-4]>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        int headingIndex = heading.Success
            ? heading.Index
            : html.IndexOf("基本情報", StringComparison.Ordinal);
        if (headingIndex < 0)
        {
            return string.Empty;
        }

        // 캐릭터 본체 이미지는 기본정보 제목 직후에 있으므로 앞쪽 광고/네비 이미지를 포함하지 않습니다.
        int start = heading.Success ? heading.Index : Math.Max(0, headingIndex - 240);
        int end = html.IndexOf("文字の使いやすさ", headingIndex, StringComparison.Ordinal);
        if (end < 0 || end <= headingIndex)
        {
            end = Math.Min(html.Length, headingIndex + 24000);
        }
        else
        {
            end = Math.Min(html.Length, end + 500);
        }

        return html[start..end];
    }

    private static string FindPreferredImageUrl(
        string html,
        string name,
        bool preferGameWith,
        bool isGameWithBasicInfoSection = false)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var candidates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     html,
                     "<(?:img|source)\\b(?<attrs>[^>]+)>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string attrs = match.Groups["attrs"].Value;
            string alt = WebUtility.HtmlDecode(ExtractAttribute(attrs, "alt"));
            foreach (string rawUrl in ExtractImageUrls(attrs))
            {
                string url = NormalizeImageUrl(WebUtility.HtmlDecode(rawUrl), preferGameWith);
                if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    continue;
                }

                int score = ScoreImageCandidate(
                    url,
                    alt,
                    name,
                    preferGameWith,
                    isGameWithBasicInfoSection,
                    match.Index,
                    ExtractNumericAttribute(attrs, "width"),
                    ExtractNumericAttribute(attrs, "height"));
                if (!candidates.TryGetValue(url, out int existingScore) || score > existingScore)
                {
                    candidates[url] = score;
                }
            }
        }

        string bestCandidate = candidates
            .Where(item => item.Value > 0)
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key.Length)
            .Select(item => item.Key)
            .FirstOrDefault() ?? string.Empty;
        if (bestCandidate.Length > 0)
        {
            return bestCandidate;
        }

        if (isGameWithBasicInfoSection)
        {
            return string.Empty;
        }

        string metaUrl = ExtractMetaContent(html, "og:image");
        string normalizedMetaUrl = NormalizeImageUrl(metaUrl, preferGameWith);
        return IsRejectedImageUrl(normalizedMetaUrl)
            ? string.Empty
            : normalizedMetaUrl;
    }

    private static IEnumerable<string> ExtractImageUrls(string attributes)
    {
        var urls = new List<string>();
        foreach (string attributeName in new[]
                 {
                     "data-original", "data-src", "data-lazy-src", "src",
                     "data-srcset", "srcset"
                 })
        {
            string value = ExtractAttribute(attributes, attributeName);
            if (value.Length == 0)
            {
                continue;
            }

            if (attributeName.EndsWith("srcset", StringComparison.Ordinal))
            {
                string srcSetUrl = ExtractLargestSrcSetUrl(value);
                if (srcSetUrl.Length > 0)
                {
                    urls.Add(srcSetUrl);
                }
            }
            else
            {
                urls.Add(value);
            }
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractLargestSrcSetUrl(string srcSet)
    {
        var candidates = srcSet
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Select(item =>
            {
                string[] parts = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int width = 0;
                if (parts.Length > 1)
                {
                    string descriptor = parts[^1].Trim();
                    if (descriptor.EndsWith('w') &&
                        int.TryParse(descriptor[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedWidth))
                    {
                        width = parsedWidth;
                    }
                }
                return new { Url = parts.Length > 0 ? parts[0] : string.Empty, Width = width };
            })
            .Where(item => item.Url.Length > 0)
            .OrderByDescending(item => item.Width)
            .ToArray();
        return candidates.FirstOrDefault()?.Url ?? string.Empty;
    }

    private static int ScoreImageCandidate(
        string url,
        string alt,
        string name,
        bool preferGameWith,
        bool isGameWithBasicInfoSection,
        int htmlIndex,
        int declaredWidth,
        int declaredHeight)
    {
        if (IsRejectedImageUrl(url) || IsRejectedImageAlt(alt))
        {
            return -1000;
        }
        if ((declaredWidth > 0 && declaredWidth < 160) ||
            (declaredHeight > 0 && declaredHeight < 135))
        {
            return -1000;
        }

        int score = 0;
        if (isGameWithBasicInfoSection)
        {
            score += 240;
            // 기본정보 제목에 가까울수록 캐릭터 본체 이미지일 가능성이 높습니다.
            score += Math.Max(0, 180 - Math.Min(htmlIndex / 18, 180));
        }
        if (url.Contains("/article_tools/kotodaman/gacha/", StringComparison.OrdinalIgnoreCase))
        {
            score += 360;
        }
        else if (url.Contains("/article_tools/kotodaman/", StringComparison.OrdinalIgnoreCase))
        {
            score += 160;
        }
        if (alt.Contains("キャラクター画像", StringComparison.Ordinal))
        {
            score += 100;
        }
        string normalizedAlt = NormalizeImageLabel(alt);
        string normalizedName = NormalizeImageLabel(name);
        if (normalizedName.Length > 0 && string.Equals(normalizedAlt, normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            score += 520;
        }
        else if (normalizedName.Length > 0 && normalizedAlt.Contains(normalizedName, StringComparison.OrdinalIgnoreCase))
        {
            score += 220;
        }
        if (url.Contains("img.gamewith.jp", StringComparison.OrdinalIgnoreCase))
        {
            score += preferGameWith ? 50 : 5;
        }
        if (url.Contains("kotodaman-db.com", StringComparison.OrdinalIgnoreCase))
        {
            score += preferGameWith ? 5 : 50;
        }
        if (Regex.IsMatch(url, "\\.(?:png|webp)(?:$|\\?)", RegexOptions.IgnoreCase))
        {
            score += 15;
        }
        if (Regex.IsMatch(url, "(?:rank|score|rating|star|zokusei|shuzoku)", RegexOptions.IgnoreCase))
        {
            score -= 300;
        }

        return score;
    }

    private static string NormalizeImageLabel(string value)
        => CleanText(value)
            .Normalize(NormalizationForm.FormKC)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("　", string.Empty, StringComparison.Ordinal)
            .Replace("の評価とステータス", string.Empty, StringComparison.Ordinal)
            .Replace("の評価とキャラ情報", string.Empty, StringComparison.Ordinal)
            .Trim();

    private static bool IsRejectedImageAlt(string alt)
    {
        string normalized = NormalizeImageLabel(alt);
        if (normalized.Length == 0)
        {
            return false;
        }

        string[] rejectedLabels =
        {
            "属性", "種族", "星6", "星5", "星4", "ランク", "評価", "ギミック",
            "アイコン", "ImagePoint", "レア度", "火属性", "水属性", "木属性",
            "光属性", "闇属性", "冥属性", "天属性"
        };
        return rejectedLabels.Any(label =>
            string.Equals(normalized, label, StringComparison.OrdinalIgnoreCase) ||
            (normalized.Length <= 18 && normalized.Contains(label, StringComparison.OrdinalIgnoreCase)));
    }

    private static int ExtractNumericAttribute(string attributes, string attributeName)
    {
        string value = ExtractAttribute(attributes, attributeName);
        Match number = Regex.Match(value, @"^\s*(?<value>\d+)(?:px)?\s*$", RegexOptions.IgnoreCase);
        return number.Success && int.TryParse(number.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    private static bool IsRejectedImageUrl(string url)
        => string.IsNullOrWhiteSpace(url) ||
           url.Contains("transparent1px", StringComparison.OrdinalIgnoreCase) ||
           url.Contains("data:image", StringComparison.OrdinalIgnoreCase) ||
           Regex.IsMatch(
               url,
               "(?:^|[/_-])(?:logo|icon|banner|bnr|loading|sprite)(?:[/_.-]|$)",
               RegexOptions.IgnoreCase);

    private static string ExtractAttribute(string attributes, string attributeName)
    {
        Match match = Regex.Match(
            attributes,
            $"\\b{Regex.Escape(attributeName)}\\s*=\\s*[\\\"'](?<value>[^\\\"']*)[\\\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static string FindLabelValue(
        IReadOnlyList<string> lines,
        string label,
        IEnumerable<string> allowedValues)
    {
        var allowed = allowedValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int index = FindLastExactLine(lines, label);
        if (index < 0)
        {
            return string.Empty;
        }

        for (int cursor = index + 1; cursor < lines.Count && cursor < index + 8; cursor++)
        {
            string candidate = lines[cursor].Trim();
            string? value = allowed.FirstOrDefault(item => string.Equals(item, candidate, StringComparison.OrdinalIgnoreCase));
            if (value is not null)
            {
                return value;
            }
        }
        return string.Empty;
    }

    private static string FindNextValue(
        IReadOnlyList<string> lines,
        string label,
        Func<string, bool> predicate)
    {
        int index = FindLastExactLine(lines, label);
        if (index < 0)
        {
            return string.Empty;
        }

        for (int cursor = index + 1; cursor < lines.Count && cursor < index + 8; cursor++)
        {
            string candidate = lines[cursor].Trim();
            if (predicate(candidate))
            {
                return candidate;
            }
        }
        return string.Empty;
    }

    private static int FindLastExactLine(IReadOnlyList<string> lines, string value)
    {
        for (int index = lines.Count - 1; index >= 0; index--)
        {
            if (string.Equals(lines[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsReasonableGroupValue(string value)
        => value.Length is > 0 and <= 40 &&
           !value.Contains("選択", StringComparison.Ordinal) &&
           !string.Equals(value, "なし", StringComparison.Ordinal);

    private static void AddKanaListLine(ISet<string> output, string text)
    {
        string compact = Regex.Replace(
            text.Normalize(NormalizationForm.FormC),
            @"[\s・･·,，、/|]+",
            string.Empty);
        if (compact.Length == 0 ||
            compact.Contains("まぁまぁ", StringComparison.Ordinal) ||
            compact.Contains("まあまあ", StringComparison.Ordinal) ||
            compact.Any(character =>
                !(character is >= 'ぁ' and <= 'ゖ') &&
                !(character is >= 'ァ' and <= 'ヶ') &&
                character != 'ー'))
        {
            return;
        }

        AddKanaTokens(output, compact);
    }

    private static void AddKanaTokens(ISet<string> output, string text)
    {
        foreach (Match match in Regex.Matches(text.Normalize(NormalizationForm.FormC), "[ぁ-ゖァ-ヶー]"))
        {
            string token = KanaUtility.ToHiraganaEquivalent(match.Value);
            if (KanaUtility.IsJapaneseCell(token))
            {
                output.Add(token);
            }
        }
    }

    private static List<string> NormalizeLetters(IEnumerable<string> letters)
        => letters
            .Select(KanaUtility.NormalizeCell)
            .Select(KanaUtility.ToHiraganaEquivalent)
            .Where(letter => letter.Length > 0 && KanaUtility.IsJapaneseCell(letter))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string MapDatabaseCategory(string rarity)
        => rarity.Trim() switch
        {
            "スペシャル" => CharacterCategories.Special,
            "レジェンド" => CharacterCategories.Legend,
            "グランド" => CharacterCategories.Grand,
            "ドリーム" => CharacterCategories.Dream,
            "ミラクル" => CharacterCategories.Miracle,
            _ => CharacterCategories.Other
        };

    private static string RemoveEpithet(string fullName)
    {
        string trimmed = fullName.Trim();
        int separator = trimmed.IndexOf('・');
        if (separator <= 0 || separator >= trimmed.Length - 1)
        {
            return trimmed;
        }

        string prefix = trimmed[..separator];
        bool looksLikeTitle = prefix.Length >= 4 && prefix.Any(character =>
            character is >= '\u3040' and <= '\u309F' ||
            character is >= '\u4E00' and <= '\u9FFF');
        return looksLikeTitle
            ? trimmed[(separator + 1)..].Trim()
            : trimmed;
    }

    private static string RemoveElementSuffix(string name)
        => Regex.Replace(name.Trim(), "[（(](?:火|水|木|光|闇|冥|天|虹)[）)]$", string.Empty);

    private static string RemoveTrailingBracketQualifier(string name)
        => Regex.Replace(
            name?.Trim() ?? string.Empty,
            @"(?:【[^】]+】|〖[^〗]+〗|\[[^\]]+\])$",
            string.Empty,
            RegexOptions.CultureInvariant).Trim();

    private static IEnumerable<string> SplitGroupNames(string value)
        => value
            .Replace("または", "・", StringComparison.Ordinal)
            .Replace("、", "・", StringComparison.Ordinal)
            .Split(new[] { '・', ',', '，', '、', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(group => group.Trim())
            .Where(group => group.Length > 0);

    private static string NormalizeDatabaseUrl(string value)
    {
        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            return "https://www.kotodaman-db.com" + value;
        }
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            ? uri.ToString()
            : string.Empty;
    }

    private static int GetNameMatchScore(string normalizedSource, string candidateText)
    {
        string candidate = NormalizeNameForComparison(RemoveEpithet(candidateText));
        if (candidate.Length == 0 || normalizedSource.Length == 0)
        {
            return 0;
        }

        if (string.Equals(candidate, normalizedSource, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        string sourceWithoutQualifier = NormalizeNameForComparison(RemoveParentheticalText(normalizedSource));
        string candidateWithoutQualifier = NormalizeNameForComparison(RemoveParentheticalText(candidateText));
        if (sourceWithoutQualifier.Length >= 2 &&
            string.Equals(sourceWithoutQualifier, candidateWithoutQualifier, StringComparison.OrdinalIgnoreCase))
        {
            return 92;
        }

        if (candidate.Contains(normalizedSource, StringComparison.OrdinalIgnoreCase) ||
            normalizedSource.Contains(candidate, StringComparison.OrdinalIgnoreCase))
        {
            int shorter = Math.Min(candidate.Length, normalizedSource.Length);
            int longer = Math.Max(candidate.Length, normalizedSource.Length);
            double coverage = longer == 0 ? 0 : (double)shorter / longer;
            return coverage >= 0.75 ? 88 : 78;
        }

        double similarity = GetNormalizedSimilarity(normalizedSource, candidate);
        if (similarity >= 0.92) return 90;
        if (similarity >= 0.84) return 82;
        if (similarity >= 0.76) return 74;
        return 0;
    }

    private static double GetNormalizedSimilarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];
        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                int insertion = current[j - 1] + 1;
                int deletion = previous[j] + 1;
                current[j] = Math.Min(substitution, Math.Min(insertion, deletion));
            }
            (previous, current) = (current, previous);
        }

        int distance = previous[right.Length];
        return 1.0 - (double)distance / Math.Max(left.Length, right.Length);
    }

    private static string RemoveParentheticalText(string value)
        => Regex.Replace(value ?? string.Empty, "[（(][^）)]*[）)]", string.Empty).Trim();

    private static string NormalizeNameForComparison(string value)
    {
        string normalized = RemoveElementSuffix(WebUtility.HtmlDecode(value ?? string.Empty))
            .Normalize(NormalizationForm.FormKC)
            .Replace("\uFE0E", string.Empty, StringComparison.Ordinal)
            .Replace("\uFE0F", string.Empty, StringComparison.Ordinal)
            .Replace("\U000E0100", string.Empty, StringComparison.Ordinal)
            .Replace("＆", "&", StringComparison.Ordinal)
            .Replace("･", "・", StringComparison.Ordinal);

        return Regex.Replace(
                normalized,
                "[\\s・･·「」『』〖〗【】［］\\[\\]（）()〈〉《》,，、./／:：;；'\\\"“”‘’!！?？_＿\\-‐‑‒–—―~〜]+",
                string.Empty)
            .ToLowerInvariant();
    }

    private static string CleanText(string htmlFragment)
    {
        string withoutTags = Regex.Replace(htmlFragment, "<[^>]+>", string.Empty);
        return Regex.Replace(WebUtility.HtmlDecode(withoutTags), "\\s+", " ").Trim();
    }

    private static string NormalizeImageUrl(string value, bool preferGameWith)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + trimmed;
        }
        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return (preferGameWith ? "https://gamewith.jp" : "https://www.kotodaman-db.com") + trimmed;
        }
        return trimmed;
    }

    private static string GuessImageExtension(Uri uri, string? mediaType)
    {
        string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is ".png" or ".jpg" or ".jpeg" or ".webp")
        {
            return extension;
        }
        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };
    }
}
