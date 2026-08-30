using KotodamanWordFinder.Themes;
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
            Title = Loc.Get("Str.Screenshot.OpenFileDialogTitle"),
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
            SetError(Loc.Get("Str.Screenshot.ImageReadFailed"));
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

                    LoadScreenshot(copy, Loc.Get("Str.Screenshot.ClipboardImageLabel"));
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

                    LoadScreenshot(bitmap, string.Format(Loc.Get("Str.Screenshot.ClipboardFileLabel"), System.IO.Path.GetFileName(filePath)));
                    return;
                }
            }

            SetError(Loc.Get("Str.Screenshot.ClipboardEmpty"));
        }
        catch (Exception exception)
        {
            SetError(string.Format(Loc.Get("Str.Screenshot.ClipboardReadFailed"), exception.Message));
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
        RecognitionSummaryText.Text = Loc.Get("Str.Screenshot.BeforeRecognition");
        GuessGridSelection();
        Controls.SetAlertBanner(StatusBanner, StatusText, string.Format(Loc.Get("Str.Screenshot.LoadedSummary"), sourceLabel, safeBitmap.PixelWidth, safeBitmap.PixelHeight), false, Theme.TextSecondary);
    }

    private void GuessGridButton_Click(object sender, RoutedEventArgs e)
        => GuessGridSelection();

    private void UseFullImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_screenshot is null)
        {
            SetError(Loc.Get("Str.Screenshot.SelectScreenshotFirst"));
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
            SetError(Loc.Get("Str.Screenshot.SelectScreenshotFirst"));
            return;
        }

        int width = _screenshot.PixelWidth;
        int height = _screenshot.PixelHeight;
        double aspect = width / (double)Math.Max(1, height);

        // GameWith에서 볼 수 있는 덱 화면처럼 6x2 카드 그리드가 화면 상·중단을 크게 차지하는 경우를 기본값으로 둡니다.
        // 기종/캡처 방식이 다르면 사용자가 드래그 한 번으로 바로 보정할 수 있습니다.
        double leftRatio;
        double topRatio;
        double widthRatio;
        double heightRatio;
        if (aspect < 0.60) // 휴대폰 세로형 전체 스크린샷 - 실제 코토다망 덱 편성 팝업 기준
        {
            // v1.25.2: 게임 UI가 4열x3행에서 6열x2행으로 바뀌면서 카드 영역이
            // 훨씬 낮고 얇아졌습니다. 실제 1080x2340 스크린샷 픽셀 좌표(88,895)-(988,1330)를
            // 기준으로 재보정했습니다.
            leftRatio = 0.08;
            topRatio = 0.383;
            widthRatio = 0.83;
            heightRatio = 0.186;
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
            // 상단 리더 특성 바와 하단 버튼을 제외한 대략 14%~84% 구간(4열x3행 시절 값).
            // TODO: 6열x2행 UI로 바뀐 뒤의 실제 에뮬레이터 캡처로 재보정 필요 - 세로 폭이 훨씬 좁아졌을 가능성이 높습니다.
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
            SetError(Loc.Get("Str.Screenshot.SelectScreenshotFirst"));
            return;
        }

        if (_selectedGridRect is not Int32Rect gridRect)
        {
            SetError(Loc.Get("Str.Screenshot.SelectRegionFirst"));
            return;
        }

        try
        {
            var recognitionWatch = Stopwatch.StartNew();
            Mouse.OverrideCursor = Cursors.Wait;
            RecognizeButton.IsEnabled = false;
            Controls.SetAlertBanner(StatusBanner, StatusText, Loc.Get("Str.Screenshot.RecognizingBusy"), false, Theme.TextSecondary);
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
                ? string.Format(Loc.Get("Str.Screenshot.LearningSampleSuffix"), learningStats.SampleCount)
                : string.Empty;
            int attributeHintCount = matches.Count(match =>
                match.AttributeConfidence >= 0.45 && !string.IsNullOrWhiteSpace(match.AttributeHint));
            string attributeText = UseAttributeColorAssistCheckBox.IsChecked == true
                ? string.Format(Loc.Get("Str.Screenshot.AttributeHintSuffix"), attributeHintCount)
                : string.Empty;
            RecognitionSummaryText.Text = string.Format(
                Loc.Get("Str.Screenshot.RecognitionCompleteSummary"),
                recognitionWatch.Elapsed.TotalSeconds.ToString("0.0"),
                averageMatches.ToString("F1"),
                autoSelectedCount,
                12 - autoSelectedCount,
                learningText,
                attributeText);
            Controls.SetAlertBanner(StatusBanner, StatusText, autoSelectedCount == 12
                ? Loc.Get("Str.Screenshot.AutoSelectDoneHint")
                : Loc.Get("Str.Screenshot.AmbiguousSlotsHint"),
                isAlert: autoSelectedCount != 12, Theme.Success);
        }
        catch (Exception exception)
        {
            SetError(string.Format(Loc.Get("Str.Screenshot.RecognitionError"), exception.Message));
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
            new(null, Loc.Get("Str.Screenshot.ConfirmNeededPlaceholder"), null)
        };

        foreach (DeckScreenshotCandidate candidate in match.Candidates)
        {
            string attributeLabel = DeckDataService.NormalizeAttribute(candidate.Character.Attribute);
            choices.Add(new CharacterChoice(
                candidate.Character,
                string.Format(
                    Loc.Get("Str.Screenshot.MatchCandidateLine"),
                    candidate.MatchCount,
                    attributeLabel.Length == 0 ? "?" : attributeLabel,
                    CharacterNameLoc.GetName(candidate.Character)),
                candidate.Similarity));
        }

        foreach (CharacterEntry character in _sortedLibrary)
        {
            if (suggested.ContainsKey(character.Id))
            {
                continue;
            }
            string attributeLabel = DeckDataService.NormalizeAttribute(character.Attribute);
            string characterDisplayName = CharacterNameLoc.GetName(character);
            choices.Add(new CharacterChoice(
                character,
                attributeLabel.Length == 0 ? characterDisplayName : $"[{attributeLabel}] {characterDisplayName}",
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
            ? string.Format(
                Loc.Get("Str.Screenshot.AttributeHintLine"),
                match.AttributeHint,
                match.AttributeConfidence.ToString("P0"),
                match.AttributeSource)
            : "";
        string summary = match.Candidates.Count == 0
            ? attributeHintText + Loc.Get("Str.Screenshot.NoRecommendation")
            : attributeHintText + string.Format(
                Loc.Get("Str.Screenshot.RecommendationList"),
                string.Join(" / ", match.Candidates.Take(3).Select(candidate =>
            {
                string attribute = DeckDataService.NormalizeAttribute(candidate.Character.Attribute);
                string marker = match.AttributeConfidence >= 0.45 &&
                                DeckScreenshotRecognitionService.CharacterSupportsAttribute(
                                    candidate.Character,
                                    match.AttributeHint)
                    ? "✓"
                    : "";
                return string.Format(
                    Loc.Get("Str.Screenshot.CandidateOptionLine"),
                    marker,
                    attribute.Length == 0 ? "?" : attribute,
                    CharacterNameLoc.GetName(candidate.Character),
                    candidate.MatchCount);
            })));

        if (!autoConfident && best is not null)
        {
            summary = string.Format(Loc.Get("Str.Screenshot.AmbiguousHoldLine"), matchMargin, summary);
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
            SetError(Loc.Get("Str.Screenshot.RunRecognitionFirst"));
            return;
        }

        int[] emptySlots = _slotViewModels
            .Where(item => item.SelectedChoice?.Character is null)
            .Select(item => item.SlotIndex + 1)
            .ToArray();
        if (emptySlots.Length > 0)
        {
            string emptySlotList = string.Join(", ", emptySlots.Select(slot =>
                string.Format(Loc.Get("Str.Screenshot.SlotNumber"), slot)));
            MessageBox.Show(
                this,
                string.Format(Loc.Get("Str.Screenshot.SlotsNotSelectedBody"), emptySlotList),
                Loc.Get("Str.Screenshot.SlotsNotSelectedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetError(Loc.Get("Str.Screenshot.AllSlotsRequired"));
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
                    CharacterEntry? matchedCharacter = _library.FirstOrDefault(character => character.Id == group.Key);
                    string name = matchedCharacter is null ? group.Key : CharacterNameLoc.GetName(matchedCharacter);
                    string slots = string.Join(", ", group.Select(item =>
                        string.Format(Loc.Get("Str.Screenshot.SlotNumber"), item.Slot)));
                    return $"• {name}: {slots}";
                }));

            MessageBox.Show(
                this,
                string.Format(Loc.Get("Str.Screenshot.DuplicateCharacterBody"), duplicateText),
                Loc.Get("Str.Screenshot.DuplicateCharacterTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            SetError(Loc.Get("Str.Screenshot.DuplicateRemainingStop"));
            return;
        }

        if (selected.Count != DeckScreenshotRecognitionService.SlotCount)
        {
            SetError(Loc.Get("Str.Screenshot.All12SlotsRequired"));
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
                    string.Format(Loc.Get("Str.Screenshot.PartialLearningFailBody"), learningResult.FailedCount),
                    Loc.Get("Str.Screenshot.PartialLearningFailTitle"),
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
                Loc.Get("Str.Screenshot.NoLearningSamplesBody"),
                Loc.Get("Str.Screenshot.NoLearningSamplesTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            string.Format(
                Loc.Get("Str.Screenshot.ConfirmResetLearningBody"),
                DeckScreenshotLearningService.CurrentUiProfileDisplayName,
                stats.SampleCount),
            Loc.Get("Str.Screenshot.ResetCurrentUiLearning"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_learningService.ClearCurrentProfile())
        {
            SetError(Loc.Get("Str.Screenshot.DeleteLearningFolderFailed"));
            return;
        }

        _recognitionService.InvalidateLearnedTemplates();
        UpdateLearningStatus();
        Controls.SetAlertBanner(StatusBanner, StatusText, Loc.Get("Str.Screenshot.ResetLearningDone"), false, Theme.TextSecondary);
    }

    private void UpdateLearningStatus()
    {
        LearningSampleStats stats = _learningService.GetStats();
        LearningStatusText.Text = string.Format(
            Loc.Get("Str.Screenshot.LearningProfileSummary"),
            DeckScreenshotLearningService.CurrentUiProfileDisplayName,
            stats.CharacterCount,
            stats.SampleCount,
            stats.SizeText);
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
            Stroke = Theme.Focus,
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
            Stroke = Theme.Info,
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
            ? string.Format(Loc.Get("Str.Screenshot.SelectionSizeSummary"), rect.Width, rect.Height, rect.Width / 4, rect.Height / 3)
            : Loc.Get("Str.Screenshot.DragFullRegionHint");
    }

    private void SetError(string message)
        => Controls.SetAlertBanner(StatusBanner, StatusText, message, true);

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
        public string SlotText => SlotIndex == 0 ? Loc.Get("Str.Screenshot.SlotLeaderText") : (SlotIndex + 1).ToString();
        public ImageSource CropThumbnail { get; }
        public IReadOnlyList<CharacterChoice> Choices { get; }
        public double BestSimilarity { get; }
        public double SecondSimilarity { get; }
        public int BestMatchCount { get; }
        public int SecondMatchCount { get; }
        public bool IsAutoConfident { get; }
        public string BestScoreText => BestMatchCount <= 0
            ? Loc.Get("Str.Screenshot.NoMatch")
            : IsAutoConfident
                ? string.Format(Loc.Get("Str.Screenshot.MatchAuto"), BestMatchCount)
                : string.Format(Loc.Get("Str.Screenshot.MatchConfirm"), BestMatchCount);
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
            ? Theme.Success
            : BestMatchCount >= 7
                ? Theme.Warn
                : Theme.Error;

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
