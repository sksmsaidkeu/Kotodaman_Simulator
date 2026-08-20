using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Services;
using Microsoft.Win32;

namespace KotodamanWordFinder;

public partial class DeckScreenshotImportWindow : Window
{
    private readonly IReadOnlyList<CharacterEntry> _library;
    private readonly IReadOnlyList<CharacterEntry> _sortedLibrary;
    private readonly string _dataDirectory;
    private readonly DeckScreenshotRecognitionService _recognitionService;
    private readonly DeckScreenshotLearningService _learningService;
    private BitmapSource? _screenshot;
    private Int32Rect? _selectedGridRect;
    private bool _isDragging;
    private Point _dragStart;
    private readonly List<DeckScreenshotSlotViewModel> _slotViewModels = new();

    public DeckScreenshotImportWindow(
        IReadOnlyList<CharacterEntry> library,
        string dataDirectory)
    {
        InitializeComponent();
        _library = library;
        _sortedLibrary = library
            .OrderBy(character => character.Name, StringComparer.Ordinal)
            .ThenBy(character => character.Id, StringComparer.Ordinal)
            .ToArray();
        _dataDirectory = dataDirectory;
        _recognitionService = new DeckScreenshotRecognitionService(dataDirectory, library);
        _learningService = new DeckScreenshotLearningService(dataDirectory);
        SizeChanged += (_, _) => DrawSelectionOverlay();
        Closed += (_, _) => _recognitionService.Dispose();
        UpdateLearningStatus();
    }

    public IReadOnlyList<string> SelectedCharacterIds { get; private set; } = Array.Empty<string>();

    private void ChooseScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "코토다망 덱 화면 스크린샷 선택",
            Filter = CharacterImageService.GetDialogFilter(),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        BitmapSource? bitmap = CharacterImageService.LoadBitmapFromPath(dialog.FileName, 0);
        if (bitmap is null)
        {
            SetError("선택한 이미지를 읽지 못했습니다.");
            return;
        }

        LoadScreenshot(bitmap, System.IO.Path.GetFileName(dialog.FileName));
    }

    private void PasteScreenshotButton_Click(object sender, RoutedEventArgs e)
        => PasteScreenshotFromClipboard();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            PasteScreenshotFromClipboard();
            e.Handled = true;
        }
    }

    private void PasteScreenshotFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                BitmapSource? clipboardImage = Clipboard.GetImage();
                if (clipboardImage is not null)
                {
                    BitmapSource copy = clipboardImage.CloneCurrentValue();
                    if (copy.CanFreeze)
                    {
                        copy.Freeze();
                    }

                    LoadScreenshot(copy, "클립보드 이미지");
                    return;
                }
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                foreach (string? filePath in files)
                {
                    if (string.IsNullOrWhiteSpace(filePath))
                    {
                        continue;
                    }

                    BitmapSource? bitmap = CharacterImageService.LoadBitmapFromPath(filePath, 0);
                    if (bitmap is null)
                    {
                        continue;
                    }

                    LoadScreenshot(bitmap, $"클립보드 파일 · {System.IO.Path.GetFileName(filePath)}");
                    return;
                }
            }

            SetError("클립보드에 붙여넣을 이미지가 없습니다. 스크린샷을 복사한 뒤 Ctrl+V를 눌러 주세요.");
        }
        catch (Exception exception)
        {
            SetError($"클립보드 이미지를 읽지 못했습니다: {exception.Message}");
        }
    }

    private void LoadScreenshot(BitmapSource bitmap, string sourceLabel)
    {
        // WPF 이미지 객체는 생성 스레드에 소속될 수 있습니다.
        // 덱 인식은 백그라운드 스레드에서 실행되므로, 창에 보관하기 전에
        // 반드시 Freeze 가능한 독립 복사본으로 만들어 스레드 간 접근을 안전하게 합니다.
        BitmapSource safeBitmap = bitmap;
        if (!safeBitmap.IsFrozen)
        {
            safeBitmap = safeBitmap.CloneCurrentValue();
            if (safeBitmap.CanFreeze)
            {
                safeBitmap.Freeze();
            }
        }

        _screenshot = safeBitmap;
        ScreenshotPreviewImage.Source = safeBitmap;
        _slotViewModels.Clear();
        RecognitionResultsItemsControl.ItemsSource = null;
        RecognitionSummaryText.Text = "인식 전";
        GuessGridSelection();
        StatusText.Text = $"{sourceLabel} · {safeBitmap.PixelWidth}×{safeBitmap.PixelHeight} · 자동 영역을 확인하고 필요하면 마우스로 다시 드래그하세요.";
        StatusText.Foreground = BrushFromHex("#AFC2D1");
    }

    private void GuessGridButton_Click(object sender, RoutedEventArgs e)
        => GuessGridSelection();

    private void UseFullImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshot is null)
        {
            SetError("스크린샷을 먼저 선택하세요.");
            return;
        }

        _selectedGridRect = new Int32Rect(0, 0, _screenshot.PixelWidth, _screenshot.PixelHeight);
        DrawSelectionOverlay();
        UpdateSelectionInfo();
    }

    private void GuessGridSelection()
    {
        if (_screenshot is null)
        {
            SetError("스크린샷을 먼저 선택하세요.");
            return;
        }

        int width = _screenshot.PixelWidth;
        int height = _screenshot.PixelHeight;
        double aspect = width / (double)Math.Max(1, height);

        // GameWith에서 볼 수 있는 덱 화면처럼 4x3 카드 그리드가 화면 상·중단을 크게 차지하는 경우를 기본값으로 둡니다.
        // 기종/캡처 방식이 다르면 사용자가 드래그 한 번으로 바로 보정할 수 있습니다.
        double leftRatio;
        double topRatio;
        double widthRatio;
        double heightRatio;
        if (aspect < 0.60) // 휴대폰 세로형 전체 스크린샷 - 실제 코토다망 덱 편성 팝업 기준
        {
            // 실제 세로형 코토다망 덱 화면의 문자 원(4열 x 3행) 간격으로 재보정.
            // 폭/높이를 너무 넓게 잡으면 열·행이 내려갈수록 카드 중심이 밀려
            // ORB와 속성색 판정이 동시에 약해지므로 문자 원 간격을 슬롯 간격에 맞춥니다.
            leftRatio = 0.045;
            topRatio = 0.363;
            widthRatio = 0.86;
            heightRatio = 0.31;
        }
        else if (aspect < 0.82) // 태블릿/넓은 세로형
        {
            leftRatio = 0.03;
            topRatio = 0.29;
            widthRatio = 0.94;
            heightRatio = 0.36;
        }
        else if (aspect > 1.45) // 가로형 캡처
        {
            leftRatio = 0.05;
            topRatio = 0.12;
            widthRatio = 0.62;
            heightRatio = 0.70;
        }
        else if (aspect >= 0.82 && aspect <= 1.25 && width <= 1600 && height <= 1600)
        {
            // Nox/에뮬레이터에서 덱 패널만 캡처한 준정사각형 이미지.
            // 상단 리더 특성 바와 하단 버튼을 제외하면 실제 4x3 카드 영역은 대략 14%~84% 구간입니다.
            leftRatio = 0.00;
            topRatio = 0.14;
            widthRatio = 1.00;
            heightRatio = 0.70;
        }
        else // 그 외 덱 영역만 잘라낸 정사각형/준정사각형 이미지
        {
            leftRatio = 0.01;
            topRatio = 0.07;
            widthRatio = 0.98;
            heightRatio = 0.79;
        }

        _selectedGridRect = ClampRect(new Int32Rect(
            (int)Math.Round(width * leftRatio),
            (int)Math.Round(height * topRatio),
            (int)Math.Round(width * widthRatio),
            (int)Math.Round(height * heightRatio)));
        DrawSelectionOverlay();
        UpdateSelectionInfo();
    }

    private void PreviewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_screenshot is null)
        {
            return;
        }

        Rect displayRect = GetDisplayedImageRect();
        if (displayRect.IsEmpty)
        {
            return;
        }

        Point point = e.GetPosition(PreviewHost);
        if (!displayRect.Contains(point))
        {
            return;
        }

        _isDragging = true;
        _dragStart = point;
        PreviewHost.CaptureMouse();
        e.Handled = true;
    }

    private void PreviewHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || _screenshot is null)
        {
            return;
        }

        Rect displayRect = GetDisplayedImageRect();
        Point current = ClampPoint(e.GetPosition(PreviewHost), displayRect);
        Point start = ClampPoint(_dragStart, displayRect);
        Rect selection = MakeRect(start, current);
        _selectedGridRect = DisplayRectToPixelRect(selection, displayRect);
        DrawSelectionOverlay();
        UpdateSelectionInfo();
    }

    private void PreviewHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        PreviewHost.ReleaseMouseCapture();
        if (_selectedGridRect is Int32Rect selected &&
            (selected.Width < 48 || selected.Height < 36))
        {
            _selectedGridRect = null;
        }
        DrawSelectionOverlay();
        UpdateSelectionInfo();
        e.Handled = true;
    }

    private async void RecognizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshot is null)
        {
            SetError("스크린샷을 먼저 선택하세요.");
            return;
        }

        if (_selectedGridRect is not Int32Rect gridRect)
        {
            SetError("마우스로 덱 12칸 전체 영역을 지정하세요.");
            return;
        }

        try
        {
            var recognitionWatch = Stopwatch.StartNew();
            Mouse.OverrideCursor = Cursors.Wait;
            RecognizeButton.IsEnabled = false;
            StatusText.Text = "캐릭터 특징을 병렬 비교하는 중입니다. 첫 인식만 ORB 캐시를 만들며, 다음부터는 더 빨라집니다.";
            StatusText.Foreground = BrushFromHex("#AFC2D1");
            BitmapSource screenshot = _screenshot;

            // CroppedBitmap/PngBitmapEncoder 같은 WPF 이미지 객체는 UI 스레드에서만 만듭니다.
            // 이후 무거운 OpenCV 비교에는 PNG 바이트만 넘겨, 백그라운드 인식 중
            // "다른 스레드가 이 개체를 소유" 예외가 발생하지 않게 합니다.
            IReadOnlyList<PreparedDeckScreenshotSlot> preparedSlots =
                DeckScreenshotRecognitionService.PrepareSlots(screenshot, gridRect);
            bool useLearnedSamples = UseLearnedSamplesCheckBox.IsChecked == true;
            bool useAttributeColorAssist = UseAttributeColorAssistCheckBox.IsChecked == true;
            IReadOnlyList<DeckScreenshotSlotMatch> matches = await Task.Run(() =>
                _recognitionService.RecognizePrepared(
                    preparedSlots,
                    candidateCount: 3,
                    useLearnedSamples: useLearnedSamples,
                    useAttributeColorAssist: useAttributeColorAssist));
            recognitionWatch.Stop();

            _slotViewModels.Clear();
            foreach (DeckScreenshotSlotMatch match in matches)
            {
                _slotViewModels.Add(CreateSlotViewModel(match));
            }

            ClearDuplicateAutomaticSelections();
            RecognitionResultsItemsControl.ItemsSource = _slotViewModels;
            double averageMatches = matches
                .Where(match => match.Candidates.Count > 0)
                .Select(match => (double)match.Candidates[0].MatchCount)
                .DefaultIfEmpty(0)
                .Average();
            int autoSelectedCount = _slotViewModels.Count(item => item.SelectedChoice?.Character is not null);
            LearningSampleStats learningStats = _learningService.GetStats();
            string learningText = UseLearnedSamplesCheckBox.IsChecked == true && learningStats.SampleCount > 0
                ? $" · 학습 {learningStats.SampleCount}장 사용"
                : string.Empty;
            int attributeHintCount = matches.Count(match =>
                match.AttributeConfidence >= 0.45 && !string.IsNullOrWhiteSpace(match.AttributeHint));
            string attributeText = UseAttributeColorAssistCheckBox.IsChecked == true
                ? $" · 속성색 힌트 {attributeHintCount}/12"
                : string.Empty;
            RecognitionSummaryText.Text =
                $"12칸 인식 완료 · {recognitionWatch.Elapsed.TotalSeconds:0.0}초 · 평균 최고 매칭 {averageMatches:F1}점 · 자동 선택 {autoSelectedCount}/12 · 확인 필요 {12 - autoSelectedCount}{learningText}{attributeText}";
            StatusText.Text = autoSelectedCount == 12
                ? "자동 선택이 완료되었습니다. 그래도 12칸을 한 번 확인한 뒤 적용하세요."
                : "특징점 매칭이 애매한 슬롯만 비워 두었습니다. 추천 3개를 먼저 확인하고, 없으면 드롭다운에서 이름으로 검색하세요.";
            StatusText.Foreground = autoSelectedCount == 12
                ? BrushFromHex("#8FE3B1")
                : BrushFromHex("#FFD27A");
        }
        catch (Exception exception)
        {
            SetError($"덱 이미지 인식 중 오류: {exception.Message}");
        }
        finally
        {
            Mouse.OverrideCursor = null;
            RecognizeButton.IsEnabled = true;
        }
    }

    private DeckScreenshotSlotViewModel CreateSlotViewModel(DeckScreenshotSlotMatch match)
    {
        Dictionary<string, DeckScreenshotCandidate> suggested = match.Candidates
            .ToDictionary(candidate => candidate.Character.Id, StringComparer.Ordinal);

        var choices = new List<CharacterChoice>
        {
            new(null, "— 확인 필요 / 이 슬롯 비우기 —", null)
        };

        foreach (DeckScreenshotCandidate candidate in match.Candidates)
        {
            string attributeLabel = DeckDataService.NormalizeAttribute(candidate.Character.Attribute);
            choices.Add(new CharacterChoice(
                candidate.Character,
                $"★ 매칭 {candidate.MatchCount}점 · [{(attributeLabel.Length == 0 ? "?" : attributeLabel)}] {candidate.Character.Name}",
                candidate.Similarity));
        }

        foreach (CharacterEntry character in _sortedLibrary)
        {
            if (suggested.ContainsKey(character.Id))
            {
                continue;
            }
            string attributeLabel = DeckDataService.NormalizeAttribute(character.Attribute);
            choices.Add(new CharacterChoice(
                character,
                attributeLabel.Length == 0 ? character.Name : $"[{attributeLabel}] {character.Name}",
                null));
        }

        DeckScreenshotCandidate? best = match.Candidates.FirstOrDefault();
        DeckScreenshotCandidate? second = match.Candidates.Skip(1).FirstOrDefault();
        int bestMatchCount = best?.MatchCount ?? 0;
        int secondMatchCount = second?.MatchCount ?? 0;
        int matchMargin = bestMatchCount - secondMatchCount;

        // 기본은 기존처럼 ORB 7개 이상 + 2순위와 3점 이상 차이일 때 자동 확정합니다.
        // 속성색이 매우 확실하고 1위만 해당 속성과 맞으면 2점 차이까지 완화합니다.
        bool normalAutoConfident = best is not null
                                   && bestMatchCount >= 7
                                   && matchMargin >= 3;
        bool bestAttributeMatches = best is not null &&
                                    match.AttributeConfidence >= 0.72 &&
                                    DeckScreenshotRecognitionService.CharacterSupportsAttribute(
                                        best.Character,
                                        match.AttributeHint);
        bool secondAttributeMatches = second is not null &&
                                      DeckScreenshotRecognitionService.CharacterSupportsAttribute(
                                          second.Character,
                                          match.AttributeHint);
        bool colorAssistedAutoConfident = best is not null
                                          && bestMatchCount >= 7
                                          && matchMargin >= 2
                                          && bestAttributeMatches
                                          && !secondAttributeMatches;
        bool autoConfident = normalAutoConfident || colorAssistedAutoConfident;

        CharacterChoice selected = autoConfident && best is not null
            ? choices.First(choice => string.Equals(
                choice.Character?.Id,
                best.Character.Id,
                StringComparison.Ordinal))
            : choices[0];

        string attributeHintText = match.AttributeConfidence >= 0.38 &&
                                   !string.IsNullOrWhiteSpace(match.AttributeHint)
            ? $"속성색 {match.AttributeHint} {match.AttributeConfidence:P0} ({match.AttributeSource}) · "
            : "";
        string summary = match.Candidates.Count == 0
            ? attributeHintText + "추천 후보 없음"
            : attributeHintText + "추천 3개: " + string.Join(" / ", match.Candidates.Take(3).Select(candidate =>
            {
                string attribute = DeckDataService.NormalizeAttribute(candidate.Character.Attribute);
                string marker = match.AttributeConfidence >= 0.45 &&
                                DeckScreenshotRecognitionService.CharacterSupportsAttribute(
                                    candidate.Character,
                                    match.AttributeHint)
                    ? "✓"
                    : "";
                return $"{marker}[{(attribute.Length == 0 ? "?" : attribute)}] {candidate.Character.Name} ({candidate.MatchCount}점)";
            }));

        if (!autoConfident && best is not null)
        {
            summary = $"⚠ 자동 확정 보류 · 1~2위 차이 {matchMargin}점 · " + summary;
        }

        return new DeckScreenshotSlotViewModel(
            match.SlotIndex,
            match.Crop,
            choices,
            selected,
            best?.Similarity ?? 0,
            second?.Similarity ?? 0,
            bestMatchCount,
            secondMatchCount,
            autoConfident,
            summary,
            _dataDirectory);
    }

    private void ClearDuplicateAutomaticSelections()
    {
        var duplicateGroups = _slotViewModels
            .Where(item => item.SelectedChoice?.Character is not null)
            .GroupBy(item => item.SelectedChoice!.Character!.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var group in duplicateGroups)
        {
            DeckScreenshotSlotViewModel keep = group
                .OrderByDescending(item => item.BestMatchCount)
                .First();
            foreach (DeckScreenshotSlotViewModel item in group)
            {
                if (!ReferenceEquals(item, keep))
                {
                    item.SelectBlank();
                }
            }
        }
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_slotViewModels.Count == 0)
        {
            SetError("먼저 12칸 인식을 실행하세요.");
            return;
        }

        int[] emptySlots = _slotViewModels
            .Where(item => item.SelectedChoice?.Character is null)
            .Select(item => item.SlotIndex + 1)
            .ToArray();
        if (emptySlots.Length > 0)
        {
            MessageBox.Show(
                this,
                "아직 캐릭터가 선택되지 않은 슬롯이 있습니다.\n\n" +
                $"확인할 슬롯: {string.Join(", ", emptySlots.Select(slot => $"{slot}번"))}\n\n" +
                "애매한 슬롯은 ★ 후보를 확인해서 직접 선택해 주세요.",
                "덱 적용 불가",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetError("12칸 모두 캐릭터를 선택해야 덱에 적용할 수 있습니다.");
            return;
        }

        List<string> selected = _slotViewModels
            .Select(item => item.SelectedChoice!.Character!.Id)
            .ToList();

        var duplicateGroups = selected
            .Select((id, index) => new { Id = id, Slot = index + 1 })
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToArray();

        if (duplicateGroups.Length > 0)
        {
            string duplicateText = string.Join(
                Environment.NewLine,
                duplicateGroups.Select(group =>
                {
                    string name = _library.FirstOrDefault(character => character.Id == group.Key)?.Name ?? group.Key;
                    string slots = string.Join(", ", group.Select(item => $"{item.Slot}번"));
                    return $"• {name}: {slots}";
                }));

            MessageBox.Show(
                this,
                "같은 캐릭터가 여러 슬롯에 선택되어 있어 적용할 수 없습니다.\n\n" +
                duplicateText +
                "\n\n중복된 슬롯 중 잘못 인식된 칸을 수정한 뒤 다시 적용하세요.",
                "중복 캐릭터 - 덱 적용 중지",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetError("중복 캐릭터가 남아 있어 덱 적용을 중지했습니다.");
            return;
        }

        if (selected.Count != DeckScreenshotRecognitionService.SlotCount)
        {
            SetError("덱은 12칸 모두 확인되어야 합니다.");
            return;
        }

        if (LearnVerifiedSamplesCheckBox.IsChecked == true)
        {
            LearningSaveResult learningResult = _learningService.SaveVerifiedSamples(
                _slotViewModels.Select(item => new VerifiedDeckSlotSample(
                    item.SelectedChoice!.Character!.Id,
                    (BitmapSource)item.CropThumbnail)));

            if (learningResult.FailedCount > 0)
            {
                MessageBox.Show(
                    this,
                    $"덱 적용은 정상 처리하지만 학습 이미지 {learningResult.FailedCount}장을 저장하지 못했습니다.",
                    "학습 일부 실패",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            UpdateLearningStatus();
        }

        SelectedCharacterIds = selected;
        DialogResult = true;
    }

    private void ClearLearningButton_Click(object sender, RoutedEventArgs e)
    {
        LearningSampleStats stats = _learningService.GetStats();
        if (stats.SampleCount == 0)
        {
            MessageBox.Show(
                this,
                "현재 UI 프로필에 저장된 학습 샘플이 없습니다.",
                "학습 데이터",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"{DeckScreenshotLearningService.CurrentUiProfileDisplayName} 학습 샘플 {stats.SampleCount}장을 삭제할까요?\n\n" +
            "캐릭터 DB 이미지는 삭제되지 않으며, 이후에는 기본 일러스트만으로 인식합니다.",
            "현재 UI 학습 초기화",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_learningService.ClearCurrentProfile())
        {
            SetError("학습 데이터 폴더를 삭제하지 못했습니다.");
            return;
        }

        _recognitionService.InvalidateLearnedTemplates();
        UpdateLearningStatus();
        StatusText.Text = "현재 UI 프로필의 학습 데이터를 초기화했습니다.";
        StatusText.Foreground = BrushFromHex("#AFC2D1");
    }

    private void UpdateLearningStatus()
    {
        LearningSampleStats stats = _learningService.GetStats();
        LearningStatusText.Text =
            $"학습 프로필: {DeckScreenshotLearningService.CurrentUiProfileDisplayName} · " +
            $"{stats.CharacterCount}명 / {stats.SampleCount}장 · {stats.SizeText}";
    }

    private void DrawSelectionOverlay()
    {
        SelectionCanvas.Children.Clear();
        if (_screenshot is null || _selectedGridRect is not Int32Rect pixelRect)
        {
            return;
        }

        Rect displayRect = GetDisplayedImageRect();
        if (displayRect.IsEmpty)
        {
            return;
        }

        Rect rect = PixelRectToDisplayRect(pixelRect, displayRect);
        var outline = new Rectangle
        {
            Width = Math.Max(1, rect.Width),
            Height = Math.Max(1, rect.Height),
            Stroke = BrushFromHex("#66D9EF"),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(28, 102, 217, 239))
        };
        Canvas.SetLeft(outline, rect.Left);
        Canvas.SetTop(outline, rect.Top);
        SelectionCanvas.Children.Add(outline);

        for (int column = 1; column < DeckScreenshotRecognitionService.ColumnCount; column++)
        {
            double x = rect.Left + rect.Width * column / DeckScreenshotRecognitionService.ColumnCount;
            AddGridLine(x, rect.Top, x, rect.Bottom);
        }
        for (int row = 1; row < DeckScreenshotRecognitionService.RowCount; row++)
        {
            double y = rect.Top + rect.Height * row / DeckScreenshotRecognitionService.RowCount;
            AddGridLine(rect.Left, y, rect.Right, y);
        }
    }

    private void AddGridLine(double x1, double y1, double x2, double y2)
    {
        SelectionCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = BrushFromHex("#9AEAF7"),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 3 }
        });
    }

    private Rect GetDisplayedImageRect()
    {
        if (_screenshot is null || PreviewHost.ActualWidth <= 0 || PreviewHost.ActualHeight <= 0)
        {
            return Rect.Empty;
        }

        double scale = Math.Min(
            PreviewHost.ActualWidth / _screenshot.PixelWidth,
            PreviewHost.ActualHeight / _screenshot.PixelHeight);
        double width = _screenshot.PixelWidth * scale;
        double height = _screenshot.PixelHeight * scale;
        return new Rect(
            (PreviewHost.ActualWidth - width) / 2,
            (PreviewHost.ActualHeight - height) / 2,
            width,
            height);
    }

    private Int32Rect DisplayRectToPixelRect(Rect selection, Rect displayRect)
    {
        if (_screenshot is null)
        {
            return new Int32Rect();
        }

        double scaleX = _screenshot.PixelWidth / displayRect.Width;
        double scaleY = _screenshot.PixelHeight / displayRect.Height;
        var rect = new Int32Rect(
            (int)Math.Round((selection.Left - displayRect.Left) * scaleX),
            (int)Math.Round((selection.Top - displayRect.Top) * scaleY),
            (int)Math.Round(selection.Width * scaleX),
            (int)Math.Round(selection.Height * scaleY));
        return ClampRect(rect);
    }

    private Rect PixelRectToDisplayRect(Int32Rect pixelRect, Rect displayRect)
    {
        if (_screenshot is null)
        {
            return Rect.Empty;
        }

        double scaleX = displayRect.Width / _screenshot.PixelWidth;
        double scaleY = displayRect.Height / _screenshot.PixelHeight;
        return new Rect(
            displayRect.Left + pixelRect.X * scaleX,
            displayRect.Top + pixelRect.Y * scaleY,
            pixelRect.Width * scaleX,
            pixelRect.Height * scaleY);
    }

    private Int32Rect ClampRect(Int32Rect rect)
    {
        if (_screenshot is null)
        {
            return rect;
        }

        int x = Math.Clamp(rect.X, 0, Math.Max(0, _screenshot.PixelWidth - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, _screenshot.PixelHeight - 1));
        int width = Math.Clamp(rect.Width, 1, _screenshot.PixelWidth - x);
        int height = Math.Clamp(rect.Height, 1, _screenshot.PixelHeight - y);
        return new Int32Rect(x, y, width, height);
    }

    private void UpdateSelectionInfo()
    {
        SelectionInfoText.Text = _selectedGridRect is Int32Rect rect
            ? $"선택 영역: {rect.Width}×{rect.Height} · 1칸 약 {rect.Width / 4}×{rect.Height / 3}px"
            : "12칸 전체 영역을 마우스로 드래그하세요.";
    }

    private void SetError(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = BrushFromHex("#FF9B9B");
    }

    private static Point ClampPoint(Point point, Rect rect)
        => new(
            Math.Clamp(point.X, rect.Left, rect.Right),
            Math.Clamp(point.Y, rect.Top, rect.Bottom));

    private static Rect MakeRect(Point first, Point second)
        => new(
            Math.Min(first.X, second.X),
            Math.Min(first.Y, second.Y),
            Math.Abs(first.X - second.X),
            Math.Abs(first.Y - second.Y));

    private static SolidColorBrush BrushFromHex(string hex)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    public sealed class CharacterChoice
    {
        public CharacterChoice(CharacterEntry? character, string displayName, double? similarity)
        {
            Character = character;
            DisplayName = displayName;
            Similarity = similarity;
        }

        public CharacterEntry? Character { get; }
        public string DisplayName { get; }
        public double? Similarity { get; }
    }

    public sealed class DeckScreenshotSlotViewModel : INotifyPropertyChanged
    {
        private readonly string _dataDirectory;
        private CharacterChoice? _selectedChoice;
        private ImageSource? _selectedCharacterThumbnail;

        public DeckScreenshotSlotViewModel(
            int slotIndex,
            ImageSource cropThumbnail,
            IReadOnlyList<CharacterChoice> choices,
            CharacterChoice selectedChoice,
            double bestSimilarity,
            double secondSimilarity,
            int bestMatchCount,
            int secondMatchCount,
            bool autoConfident,
            string candidateSummary,
            string dataDirectory)
        {
            SlotIndex = slotIndex;
            CropThumbnail = cropThumbnail;
            Choices = choices;
            _selectedChoice = selectedChoice;
            _dataDirectory = dataDirectory;
            BestSimilarity = bestSimilarity;
            SecondSimilarity = secondSimilarity;
            BestMatchCount = bestMatchCount;
            SecondMatchCount = secondMatchCount;
            IsAutoConfident = autoConfident;
            CandidateSummary = candidateSummary;
            RefreshSelectedCharacterThumbnail();
        }

        public int SlotIndex { get; }
        public string SlotText => SlotIndex == 0 ? "1\n리더" : (SlotIndex + 1).ToString();
        public ImageSource CropThumbnail { get; }
        public IReadOnlyList<CharacterChoice> Choices { get; }
        public double BestSimilarity { get; }
        public double SecondSimilarity { get; }
        public int BestMatchCount { get; }
        public int SecondMatchCount { get; }
        public bool IsAutoConfident { get; }
        public string BestScoreText => BestMatchCount <= 0
            ? "매칭 없음"
            : IsAutoConfident
                ? $"매칭 {BestMatchCount} · 자동"
                : $"매칭 {BestMatchCount} · 확인";
        public string CandidateSummary { get; }
        public ImageSource? SelectedCharacterThumbnail
        {
            get => _selectedCharacterThumbnail;
            private set
            {
                if (ReferenceEquals(_selectedCharacterThumbnail, value))
                {
                    return;
                }

                _selectedCharacterThumbnail = value;
                OnPropertyChanged();
            }
        }

        public Brush ScoreBrush => IsAutoConfident
            ? BrushFromHex("#8FE3B1")
            : BestMatchCount >= 7
                ? BrushFromHex("#FFD27A")
                : BrushFromHex("#FF9B9B");

        public void SelectBlank()
        {
            SelectedChoice = Choices.FirstOrDefault(choice => choice.Character is null);
        }

        public CharacterChoice? SelectedChoice
        {
            get => _selectedChoice;
            set
            {
                if (ReferenceEquals(_selectedChoice, value))
                {
                    return;
                }
                _selectedChoice = value;
                OnPropertyChanged();
                RefreshSelectedCharacterThumbnail();
            }
        }

        private void RefreshSelectedCharacterThumbnail()
        {
            CharacterEntry? character = _selectedChoice?.Character;
            SelectedCharacterThumbnail = character is null
                ? null
                : CharacterImageService.LoadBitmap(
                    _dataDirectory,
                    character.GetActiveImageFileName(),
                    96);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
