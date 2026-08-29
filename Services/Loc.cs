using System.Windows;

namespace KotodamanWordFinder.Services;

// 코드비하인드에서 Strings.{lang}.xaml 리소스를 조회하는 최소 헬퍼.
// XAML은 DynamicResource로 직접 바인딩되므로 언어가 바뀔 때 자동 갱신되지만,
// 코드에서 .Text/.Content 로 직접 대입하는 자리는 이 헬퍼를 거쳐야 언어 전환이 반영된다.
public static class Loc
{
    public static string Get(string key) => Application.Current.Resources[key] as string ?? key;

    // ko/ja/en 판별 로직의 유일한 출처. App.ApplyLanguage와 MainWindow의 복원/토글이
    // 전부 이걸 거쳐야, 셋 중 하나만 따로 고쳐서 "토글 버튼 표시"와 "실제 로드된 언어"가
    // 어긋나는 사고를 막을 수 있다.
    public static string NormalizeLanguage(string? language) => language switch
    {
        "ja" => "ja",
        "en" => "en",
        _ => "ko"
    };

    public static string Abbreviation(string language) => language switch
    {
        "ja" => "JPN",
        "en" => "ENG",
        _ => "KOR"
    };
}
