namespace KotodamanWordFinder.Models;

public sealed class WordEntry
{
    public string Text { get; set; } = string.Empty;

    // 비워 두면 Text를 유니코드 문자 단위로 자동 분리합니다.
    // 특수한 단어만 직접 칸 배열을 지정하면 됩니다.
    public List<string>? Cells { get; set; }

    public string? Description { get; set; }

    // BuiltIn, User, GACCAG 등 데이터 출처를 표시합니다.
    public string? Source { get; set; }
}
