using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace KotodamanWordFinder.Themes;

/// <summary>
/// Controls.xaml 의 코드비하인드. 형태 스타일이 스스로 처리할 수 없는 동작만 여기 둡니다.
/// (색은 여전히 Kotodama.xaml 하나가 출처이고 여기에는 색이 없습니다.)
/// </summary>
public partial class Controls : ResourceDictionary
{
    public Controls() => InitializeComponent();

    /// <summary>
    /// P7 라벨 뱃지를 만든다. 면·선·글자를 한 번에 받는 이유는 세 톤이 흩어지면
    /// 밝은 면에 밝은 글자가 얹히기 때문이다. 모양은 LabelBadgeStyle 이 갖는다.
    /// </summary>
    public static Label LabelBadge(string text, Brush face, Brush line, Brush foreground)
        => new()
        {
            Style = (Style)Application.Current.Resources["LabelBadgeStyle"],
            Content = text,
            Background = face,
            BorderBrush = line,
            Foreground = foreground,
            Margin = new Thickness(0, 0, 4, 0)
        };

    /// <summary>
    /// P8 경고 배너. 6개 창이 공유하는 상태줄 하나(Border+TextBlock)의 배경·테두리·글자색을
    /// 한 번에 갈아끼운다. isAlert=false(성공/정보)면 지금까지처럼 배너 없이 글자색만 바뀌고,
    /// isAlert=true(오류/경고)면 Alert.Face/Alert.Line/Alert.Text 세 톤이 전부 세트로 켜진다.
    /// foreground 는 isAlert=false 일 때만 쓴다 - 경고는 항상 Alert.Text 로 고정해 두 상태가
    /// 섞이지 않게 한다. 호출부가 배너와 텍스트만 따로 대입하던 것이 P7 뱃지 때와 같은 사고
    /// (밝은 면에 밝은 글자)를 냈으므로, 세 톤을 반드시 이 함수 하나로만 바꾼다.
    /// </summary>
    public static void SetAlertBanner(Border banner, TextBlock text, string message, bool isAlert, Brush? foreground = null)
    {
        text.Text = message;
        if (isAlert)
        {
            banner.Background = Theme.AlertFace;
            banner.BorderBrush = Theme.AlertLine;
            banner.BorderThickness = new Thickness(1, 3, 1, 1);
            banner.Padding = new Thickness(12, 8, 12, 8);
            text.Foreground = Theme.AlertText;
            return;
        }

        // 스타일 기본값(투명/두께 0/Padding 0)으로 되돌린다. Brushes.Transparent 를 새로
        // 대입하지 않는 이유는 내장 브러시 하드코딩이기 때문이다(ReleaseTools/check_theme.sh 대상).
        banner.ClearValue(Border.BackgroundProperty);
        banner.ClearValue(Border.BorderBrushProperty);
        banner.ClearValue(Border.BorderThicknessProperty);
        banner.ClearValue(Border.PaddingProperty);
        text.Foreground = foreground ?? Theme.TextPrimary;
    }

    /// <summary>
    /// 모달 카드 크롬의 우상단 X. WindowStyle=None 이라 네이티브 닫기 버튼이 없으므로
    /// 여기서 대신 닫습니다. 창마다 핸들러를 복사하지 않으려고 딕셔너리에 둡니다.
    /// </summary>
    private void ModalClose_Click(object sender, RoutedEventArgs e)
        => Window.GetWindow((DependencyObject)sender)?.Close();

    /// <summary>
    /// WindowStyle=None + AllowsTransparency 창은 최대화하면 모니터 전체를 덮어
    /// 작업표시줄이 가려집니다(실측: 1920x1020 작업영역에 1936x1096 로 최대화됨).
    /// WM_GETMINMAXINFO 로 최대화 위치·크기를 그 모니터의 작업영역으로 잡아 줍니다.
    /// </summary>
    private void ModalWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (PresentationSource.FromVisual((Window)sender) is HwndSource source)
        {
            source.RemoveHook(ClampMaximizeToWorkArea);
            source.AddHook(ClampMaximizeToWorkArea);
        }
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private static IntPtr ClampMaximizeToWorkArea(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        // 값은 물리 픽셀이고, 위치는 모니터 원점 기준 상대 좌표여야 합니다.
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
        info.MaxPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
        info.MaxSize.X = monitorInfo.Work.Right - monitorInfo.Work.Left;
        info.MaxSize.Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
        Marshal.StructureToPtr(info, lParam, true);
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }
}
