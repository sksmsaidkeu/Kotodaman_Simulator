using System.Windows.Media;

namespace KotodamanWordFinder.Themes;

/// <summary>
/// 게임 데이터에 딸린 색. <see cref="Theme"/> 토큰과 성격이 다르므로 섞지 않습니다.
///
/// 속성 색(火=빨강)과 확률 램프는 크림 테마로 바꿔도 그대로 둡니다. 의미가 색 자체에
/// 실려 있어서 팔레트를 갈아끼우면 뜻이 깨집니다 — 火가 크림색이 될 수는 없고,
/// 확률 램프는 4단계의 순서가 정보입니다.
///
/// 속성 스위치는 MainWindow 와 DeckEditorWindow 에 같은 내용으로 복붙돼 있던 것을
/// 여기로 합쳤습니다. 폴백만 호출부마다 달라서 인자로 받습니다.
/// </summary>
internal static class DataPalette
{
    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static readonly Dictionary<string, SolidColorBrush> AttributeBrushes = new(StringComparer.Ordinal)
    {
        ["火"] = Frozen("#FF6B6B"),
        ["水"] = Frozen("#63B3FF"),
        ["木"] = Frozen("#70D98B"),
        ["光"] = Frozen("#FFE27A"),
        ["闇"] = Frozen("#B995FF"),
        ["天"] = Frozen("#8FE9FF"),
        ["冥"] = Frozen("#C58A5A"),
        ["虹"] = Frozen("#FF9EDB"),
    };

    public static Brush Attribute(string attribute, Brush fallback)
        => AttributeBrushes.TryGetValue(attribute, out var brush) ? brush : fallback;

    public static Color AttributeColor(string attribute, Color fallback)
        => AttributeBrushes.TryGetValue(attribute, out var brush) ? brush.Color : fallback;

    // 첫 턴 성공률 램프. 높음 → 없음 순서가 정보이므로 단계를 재배치하지 마세요.
    private static readonly SolidColorBrush HighFace = Frozen("#2C6B55");
    private static readonly SolidColorBrush HighLine = Frozen("#63C99A");
    private static readonly SolidColorBrush MidFace = Frozen("#53632D");
    private static readonly SolidColorBrush MidLine = Frozen("#A8C95A");
    private static readonly SolidColorBrush LowFace = Frozen("#6B4B2C");
    private static readonly SolidColorBrush LowLine = Frozen("#D69A55");
    private static readonly SolidColorBrush NoneFace = Frozen("#3A4250");
    private static readonly SolidColorBrush NoneLine = Frozen("#687487");

    public static (Brush Face, Brush Line) Probability(double rate) => rate switch
    {
        >= 0.80 => (HighFace, HighLine),
        >= 0.50 => (MidFace, MidLine),
        >= 0.20 => (LowFace, LowLine),
        _ => (NoneFace, NoneLine),
    };
}
