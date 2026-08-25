using System.Windows;
using System.Windows.Media;

namespace KotodamanWordFinder.Themes;

/// <summary>
/// Themes/Kotodama.xaml 의 색 토큰을 코드비하인드에서 쓰기 위한 창구입니다.
/// 값은 여기에 적지 않습니다. 색의 유일한 출처는 Kotodama.xaml 이고 이 클래스는 참조만 합니다.
///
/// 필드가 아니라 프로퍼티인 이유: static 필드 초기화는 App 리소스가 로드되기 전에 돌 수 있어
/// null 이 됩니다(App.Main 이 VelopackApp.Run 을 먼저 실행합니다). 룩업 비용은 무시 가능합니다.
/// 브러시는 딕셔너리에서 Freeze 되어 공유되므로 호출부에서 변형하면 안 됩니다.
/// </summary>
internal static class Theme
{
    private static SolidColorBrush B(string key)
        => (SolidColorBrush)Application.Current.Resources[key];

    public static SolidColorBrush Backdrop    => B("Bg.Backdrop");
    public static SolidColorBrush Panel       => B("Bg.Panel");
    public static SolidColorBrush PanelInner  => B("Bg.PanelInner");
    public static SolidColorBrush Field       => B("Bg.Field");
    public static SolidColorBrush Ivory       => B("Bg.Ivory");

    public static SolidColorBrush Line        => B("Line.Tan");
    public static SolidColorBrush LineStrong  => B("Line.TanStrong");

    public static SolidColorBrush TextPrimary   => B("Text.Primary");
    public static SolidColorBrush TextSecondary => B("Text.Secondary");
    public static SolidColorBrush TextOnAccent  => B("Text.OnAccent");

    public static SolidColorBrush Orange      => B("Accent.Orange");
    public static SolidColorBrush OrangeDeep  => B("Accent.OrangeDeep");

    public static SolidColorBrush Pink        => B("Accent.Pink");
    public static SolidColorBrush PinkSoft    => B("Accent.PinkSoft");
    public static SolidColorBrush PinkLine    => B("Accent.PinkLine");
    public static SolidColorBrush PinkPale    => B("Accent.PinkPale");

    public static SolidColorBrush BlueFace    => B("Accent.BlueFace");
    public static SolidColorBrush BlueLine    => B("Accent.BlueLine");
    public static SolidColorBrush BlueText    => B("Accent.BlueText");

    public static SolidColorBrush Focus       => B("Accent.Focus");

    public static SolidColorBrush Chip        => B("Chip.Dark");
    public static SolidColorBrush LevelNavy   => B("Level.Navy");
    public static SolidColorBrush LevelNumber => B("Level.Number");

    public static SolidColorBrush Success     => B("Status.Success");
    public static SolidColorBrush Info        => B("Status.Info");
    public static SolidColorBrush Warn        => B("Status.Warn");
    public static SolidColorBrush Error       => B("Status.Error");
    public static SolidColorBrush Special     => B("Status.Special");

    public static SolidColorBrush TextTertiary => B("Text.Tertiary");
    public static SolidColorBrush LineSoft     => B("Line.Soft");

    public static SolidColorBrush AlertFace     => B("Alert.Face");
    public static SolidColorBrush AlertLine     => B("Alert.Line");
    public static SolidColorBrush AlertText     => B("Alert.Text");

    public static SolidColorBrush RoseFace     => B("Rose.Face");
    public static SolidColorBrush RoseLine     => B("Rose.Line");
    public static SolidColorBrush RoseText     => B("Rose.Text");

    public static SolidColorBrush SpecialFace  => B("Special.Face");
    public static SolidColorBrush SpecialLine  => B("Special.Line");

    public static SolidColorBrush SuccessFace  => B("Success.Face");
    public static SolidColorBrush SuccessLine  => B("Success.Line");

    public static SolidColorBrush InfoFace     => B("Info.Face");
    public static SolidColorBrush InfoLine     => B("Info.Line");

    public static SolidColorBrush OverlayScrim   => B("Overlay.Scrim");
    public static SolidColorBrush OverlaySpecial => B("Overlay.Special");
    public static SolidColorBrush OverlayAlert    => B("Overlay.Alert");

    /// <summary>
    /// 면 색이 실행 중에 정해지는 자리(결과 뱃지 등)의 글자색을 고른다.
    /// 테마 토큰 면은 크림에서 밝고, DataPalette 의 확률 램프 면은 다크로 남으므로
    /// 한쪽으로 고정할 수 없다. 밝기를 재서 고르면 팔레트를 뒤집어도 따라온다.
    /// </summary>
    public static SolidColorBrush OnFace(Brush face)
    {
        if (face is not SolidColorBrush solid) return TextPrimary;
        Color c = solid.Color;
        double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        return luminance > 0.5 ? TextPrimary : TextOnAccent;
    }
}
