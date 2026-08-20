using System.Globalization;
using System.Text;

namespace KotodamanWordFinder.Utilities;

public static class KanaUtility
{
    public static string NormalizeCell(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().Normalize(NormalizationForm.FormC);
    }

    // 히라가나/가타카나 범위(탁음·반탁음·장음 포함)만 판면 칸 문자로 인정합니다.
    public static bool IsJapaneseCell(string cell)
    {
        if (string.IsNullOrEmpty(cell))
        {
            return false;
        }

        foreach (Rune rune in cell.EnumerateRunes())
        {
            int codePoint = rune.Value;
            bool isKana = codePoint is >= 0x3040 and <= 0x30FF;
            if (!isKana)
            {
                return false;
            }
        }

        return true;
    }

    // 가타카나(ァ~ヶ)를 대응하는 히라가나로 통일해, 검색 시 두 표기를 같은 문자로 취급합니다.
    public static string ToHiraganaEquivalent(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            builder.Append(character is >= 'ァ' and <= 'ヶ'
                ? (char)(character - 0x60)
                : character);
        }

        return builder.ToString();
    }

    // 코토다망의 문자 칸에는 장음부호가 없어서 이름의 'ー'는 검색에서 접습니다.
    // 예: 'しゃろっと'로 'シャーロット'를, '샤롯토'로도 같은 이름을 찾습니다.
    // 작은 가나(ゃゅょっ 등)는 접지 않습니다. 'うきょ'와 'うきよ'는 다른 발음이라
    // 접으면 서로 다른 캐릭터가 같은 검색어에 걸립니다.
    private static readonly char[] ProlongedSoundMarks = { 'ー', '〜', '～' };

    public static string ToSearchKey(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string hiragana = ToHiraganaEquivalent(text.Normalize(NormalizationForm.FormC));

        // 대부분의 이름에는 장음부호가 없으므로 있을 때만 새 문자열을 만듭니다.
        return hiragana.IndexOfAny(ProlongedSoundMarks) < 0
            ? hiragana
            : string.Concat(hiragana.Split(ProlongedSoundMarks));
    }

    // 한글 자모(오십음도 표기 관용)를 그대로 대응하는 가나로 바꿉니다.
    // つ는 흔히 쓰는 '츠' 표기를 기준으로 삼았습니다('쓰'가 필요하면 이 표를 바꾸면 됩니다).
    // ん은 '응'으로, 요음(캬/쵸 등)은 두 글자 가나(きゃ/ちょ 등)로 대응합니다.
    // っ(촉음)과 장음 표기(ー)는 문맥 의존적이라 이번 표에는 포함하지 않았습니다.
    private static readonly IReadOnlyDictionary<char, string> HangulToKanaMap = BuildHangulToKanaMap();
    private static readonly IReadOnlyDictionary<string, string> KanaToHangulMap = BuildKanaToHangulMap();

    private static Dictionary<char, string> BuildHangulToKanaMap()
    {
        var map = new Dictionary<char, string>();

        void Add(string hangul, string kana) => map[hangul[0]] = kana;

        // あ행
        Add("아", "あ"); Add("이", "い"); Add("우", "う"); Add("에", "え"); Add("오", "お");
        // か행
        Add("카", "か"); Add("키", "き"); Add("쿠", "く"); Add("케", "け"); Add("코", "こ");
        // さ행
        Add("사", "さ"); Add("시", "し"); Add("스", "す"); Add("세", "せ"); Add("소", "そ");
        // た행
        Add("타", "た"); Add("치", "ち"); Add("츠", "つ"); Add("테", "て"); Add("토", "と");
        // な행
        Add("나", "な"); Add("니", "に"); Add("누", "ぬ"); Add("네", "ね"); Add("노", "の");
        // は행
        Add("하", "は"); Add("히", "ひ"); Add("후", "ふ"); Add("헤", "へ"); Add("호", "ほ");
        // ま행
        Add("마", "ま"); Add("미", "み"); Add("무", "む"); Add("메", "め"); Add("모", "も");
        // や행
        Add("야", "や"); Add("유", "ゆ"); Add("요", "よ");
        // ら행
        Add("라", "ら"); Add("리", "り"); Add("루", "る"); Add("레", "れ"); Add("로", "ろ");
        // わ행 · ん
        Add("와", "わ"); Add("응", "ん");
        // が행
        Add("가", "が"); Add("기", "ぎ"); Add("구", "ぐ"); Add("게", "げ"); Add("고", "ご");
        // ざ행
        Add("자", "ざ"); Add("지", "じ"); Add("즈", "ず"); Add("제", "ぜ"); Add("조", "ぞ");
        // だ행(ぢ/づ는 じ/ず와 겹쳐 생략)
        Add("다", "だ"); Add("데", "で"); Add("도", "ど");
        // ば행
        Add("바", "ば"); Add("비", "び"); Add("부", "ぶ"); Add("베", "べ"); Add("보", "ぼ");
        // ぱ행
        Add("파", "ぱ"); Add("피", "ぴ"); Add("푸", "ぷ"); Add("페", "ぺ"); Add("포", "ぽ");

        // 요음
        Add("캬", "きゃ"); Add("큐", "きゅ"); Add("쿄", "きょ");
        Add("샤", "しゃ"); Add("슈", "しゅ"); Add("쇼", "しょ");
        Add("챠", "ちゃ"); Add("츄", "ちゅ"); Add("쵸", "ちょ");
        Add("냐", "にゃ"); Add("뉴", "にゅ"); Add("뇨", "にょ");
        Add("햐", "ひゃ"); Add("휴", "ひゅ"); Add("효", "ひょ");
        Add("먀", "みゃ"); Add("뮤", "みゅ"); Add("묘", "みょ");
        Add("랴", "りゃ"); Add("류", "りゅ"); Add("료", "りょ");
        Add("갸", "ぎゃ"); Add("규", "ぎゅ"); Add("교", "ぎょ");
        Add("쟈", "じゃ"); Add("쥬", "じゅ"); Add("죠", "じょ");
        Add("뱌", "びゃ"); Add("뷰", "びゅ"); Add("뵤", "びょ");
        Add("퍄", "ぴゃ"); Add("퓨", "ぴゅ"); Add("표", "ぴょ");

        return map;
    }

    private static Dictionary<string, string> BuildKanaToHangulMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((char hangul, string kana) in HangulToKanaMap)
        {
            // 같은 가나에 여러 한글 표기가 있을 수 있으므로 먼저 등록된 일반 표기를 유지합니다.
            map.TryAdd(kana, hangul.ToString());
        }

        // 이름 검색에서 자주 만나는 촉음/장음은 별도 처리합니다.
        map.TryAdd("っ", "");
        map.TryAdd("ー", "");
        map.TryAdd("を", "오");
        map.TryAdd("ぢ", "지");
        map.TryAdd("づ", "즈");
        map.TryAdd("ゔ", "부");
        return map;
    }

    private const int HangulSyllableBase = 0xAC00;
    private const int HangulSyllableLast = 0xD7A3;
    private const int HangulFinalCount = 28;
    private const int HangulFinalNieun = 4; // 받침 ㄴ
    private const int HangulFinalSiot = 19; // 받침 ㅅ

    public static string ConvertHangulToKana(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            builder.Append(ConvertHangulCharacter(character));
        }

        return builder.ToString();
    }

    public static string ConvertKanaToHangul(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        string hiragana = ToHiraganaEquivalent(text.Normalize(NormalizationForm.FormC));
        var builder = new StringBuilder(hiragana.Length);

        for (int index = 0; index < hiragana.Length;)
        {
            // 요음(きゃ 등)을 먼저 검사합니다.
            if (index + 1 < hiragana.Length)
            {
                string pair = hiragana.Substring(index, 2);
                if (KanaToHangulMap.TryGetValue(pair, out string? pairHangul))
                {
                    builder.Append(pairHangul);
                    index += 2;
                    continue;
                }
            }

            string single = hiragana[index].ToString();
            if (KanaToHangulMap.TryGetValue(single, out string? hangul))
            {
                builder.Append(hangul);
            }
            else if (single == "っ")
            {
                // 촉음 자체는 한글 음절이 아니므로 검색 별칭에서는 생략합니다.
            }
            else if (single == "ー")
            {
                // 장음부호는 앞 음절을 늘리는 표시라 검색용 한글 별칭에서는 생략합니다.
            }
            else
            {
                builder.Append(single);
            }

            index++;
        }

        return builder.ToString();
    }

    // 받침 ㄴ은 뒤에 ん을, 받침 ㅅ은 뒤에 っ을 붙입니다(예: 카렌→かれん, 아갓토→あがっと).
    // 겹받침(ㄵ, ㄶ 등)은 대상이 아닙니다.
    private static string ConvertHangulCharacter(char character)
    {
        if (HangulToKanaMap.TryGetValue(character, out string? exact))
        {
            return exact;
        }

        if (character is < (char)HangulSyllableBase or > (char)HangulSyllableLast)
        {
            return character.ToString();
        }

        int finalIndex = (character - HangulSyllableBase) % HangulFinalCount;
        string? suffix = finalIndex switch
        {
            HangulFinalNieun => "ん",
            HangulFinalSiot => "っ",
            _ => null
        };

        if (suffix is null)
        {
            return character.ToString();
        }

        char baseCharacter = (char)(character - finalIndex);
        return HangulToKanaMap.TryGetValue(baseCharacter, out string? baseKana)
            ? baseKana + suffix
            : character.ToString();
    }

    // きゃ는 [き, ゃ], が는 [が]로 분리됩니다.
    // 즉 작은 ゃゅょ와 탁음·반탁음을 모두 별도 문자로 취급합니다.
    public static IReadOnlyList<string> SplitIntoCells(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        string normalized = text.Trim().Normalize(NormalizationForm.FormC);
        var cells = new List<string>();

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            // 공백과 구분자는 단어 칸으로 세지 않습니다.
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                // FormC 이후에도 결합 문자가 남는 예외는 앞 칸에 붙입니다.
                if (cells.Count > 0)
                {
                    cells[^1] = (cells[^1] + rune).Normalize(NormalizationForm.FormC);
                }
                continue;
            }

            cells.Add(rune.ToString());
        }

        return cells;
    }
}
