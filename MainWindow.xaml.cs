using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Services;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder;

public partial class MainWindow : Window
{
    private const int BoardSize = 7;
    private const int HandSize = 4;
    private const int MaximumBoardHistory = 30;
    private const int HandModeShiftHoldDurationMs = 2200;

    private static readonly KanaRow[] BasicKanaRows =
    {
        new("あ행", "あ い う え お"),
        new("か행", "か き く け こ"),
        new("さ행", "さ し す せ そ"),
        new("た행", "た ち つ て と"),
        new("な행", "な に ぬ ね の"),
        new("は행", "は ひ ふ へ ほ"),
        new("ま행", "ま み む め も"),
        new("や행", "や ゆ よ"),
        new("ら행", "ら り る れ ろ"),
        new("わ행", "わ を ん")
    };

    private static readonly KanaRow[] VoicedKanaRows =
    {
        new("が행", "が ぎ ぐ げ ご"),
        new("ざ행", "ざ じ ず ぜ ぞ"),
        new("だ행", "だ ぢ づ で ど"),
        new("ば행", "ば び ぶ べ ぼ"),
        new("ぱ행", "ぱ ぴ ぷ ぺ ぽ")
    };

    private static readonly KanaRow[] SmallKanaRows =
    {
        new("소문자", "ぁ ぃ ぅ ぇ ぉ"),
        new("요음·촉음", "ゃ ゅ ょ っ ゎ"),
        new("기타", "ー ゔ")
    };

    private readonly string?[] _boardCells = new string?[BoardSize];
    private readonly List<Button> _boardButtons = new();
    private readonly Stack<string?[]> _boardHistory = new();
    private readonly Dictionary<string, Button> _characterButtons =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CharacterEntry> _deckById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _deckIndexById =
        new(StringComparer.Ordinal);
    private readonly List<string> _selectedHandCharacterIds = new();
    private readonly Dictionary<string, string> _selectedHandLetterStateIds =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _selectedHandFormIds =
        new(StringComparer.Ordinal);
    private readonly DispatcherTimer _autoSearchTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _handModeShiftHoldTimer;
    private IReadOnlyList<CharacterEntry>? _modeShiftLibraryCache;
    private int _pendingHandModeShiftIndex = -1;
    private DateTime _handModeShiftHoldStartedUtc;
    private ProgressBar? _pendingHandModeShiftProgress;
    private Border? _pendingHandModeShiftFrame;

    private IReadOnlyList<CharacterEntry> _deck = Array.Empty<CharacterEntry>();
    private IReadOnlyList<DeckPreset> _mainPresets = Array.Empty<DeckPreset>();
    private string? _selectedMainPresetId;
    private bool _isRefreshingMainPresets;
    private WordSearchService _searchService = null!;
    private int _activeBoardIndex;
    private bool _isInitializing = true;
    private string _dataDirectory = string.Empty;
    private int _loadedWordCount;
    private int _loadedSearchWordCount;
    private int _loadedShortWordCount;
    private int _loadedGaccagWordCount;
    private int _loadedGaccagSearchWordCount;
    private int _loadedGaccagShortWordCount;
    private int _loadedGaccagFourLetterCount;
    private int _loadedGaccagFiveLetterCount;
    private int _loadedGaccagSixLetterCount;
    private int _loadedGaccagSevenLetterCount;
    private bool _hasCompleteComboData;
    private CancellationTokenSource? _dictionaryUpdateCts;
    private bool _isUpdatingDictionary;
    private IReadOnlyDictionary<int, SearchGroup> _lastGeneralResults = CreateEmptySearchGroups();
    private IReadOnlyDictionary<int, SearchGroup> _lastHandResults = CreateEmptySearchGroups();
    private IReadOnlyDictionary<int, SearchGroup> _lastDeckResults = CreateEmptySearchGroups();
    private int _selectedGeneralWordLength = 7;
    private int _selectedHandWordLength = 7;
    private int _selectedDeckWordLength = 7;
    private string _lastGeneralSearchSignature = string.Empty;
    private string _lastHandSearchSignature = string.Empty;
    private string _lastDeckSearchSignature = string.Empty;
    private string _lastHandEmptyMessage = "판면과 현재 손패를 선택하면 자동으로 검색합니다.";
    private string _lastDeckEmptyMessage = "판면을 기준으로 덱 전체의 가능한 단어도 함께 표시됩니다.";
    private bool _hasPerformedSearch;
    private bool _isRenderingSelectedHand;
    private string _deckResultSortMode = "Practical";

    public MainWindow()
    {
        _autoSearchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _autoSearchTimer.Tick += AutoSearchTimer_Tick;

        _settingsSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveSettingsImmediatelySafely();
        };

        _handModeShiftHoldTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _handModeShiftHoldTimer.Tick += HandModeShiftHoldTimer_Tick;

        InitializeComponent();
        Title = $"{AppPaths.DisplayName} v{AppPaths.AppVersion}";
        Stopwatch startupWatch = Stopwatch.StartNew();

        try
        {
            _dataDirectory = AppPaths.UserDataDirectory;

            BuildBoardButtons();
            BuildKanaSection(BasicKanaPanel, "기본 오십음도", BasicKanaRows);
            BuildKanaSection(VoicedKanaPanel, "탁음 · 반탁음", VoicedKanaRows);
            BuildKanaSection(SmallKanaPanel, "소문자 · 특수", SmallKanaRows, includeBlankButton: true);

            LoadDataFromDisk();
            RestoreSettings();
            UpdateBoardButtons();
            UpdateCharacterButtons();
            RenderSelectedHandSlots();
            RenderInitialMessages();

            _isInitializing = false;
            ScheduleAutoSearch();

            startupWatch.Stop();
            DataInitializationResult initialization = AppPaths.LastInitializationResult;
            string initializationText = initialization.CreatedUserData
                ? " · 사용자 데이터 분리 완료"
                : initialization.AddedBundledCharacterCount > 0
                    ? $" · 신규 캐릭터 {initialization.AddedBundledCharacterCount:N0}명 병합"
                    : string.Empty;

            StatusText.Text =
                $"단어 {_loadedWordCount:N0}개 · 덱 캐릭터 {_deck.Count:N0}명 · " +
                $"시작 {startupWatch.Elapsed.TotalSeconds:0.0}초{initializationText}";

            _ = Task.Run(MigrateGaccagJsonToGzipSafely);
        }
        catch (Exception exception)
        {
            AppLog.Error("데이터를 불러오는 중 오류가 발생했습니다.", exception);
            throw new InvalidOperationException(
                $"데이터를 불러오는 중 오류가 발생했습니다. {exception.Message}",
                exception);
        }

        Closing += MainWindow_Closing;
    }

    private void LoadDataFromDisk()
    {
        string wordsJsonPath = AppPaths.GetBundledDataPath("words.json");
        string wordsTextPath = AppPaths.GetBundledDataPath("words.txt");
        string gaccagWordsPath = AppPaths.ResolveUserOrBundledDataPath("gaccag_words.json");
        string deckPath = Path.Combine(_dataDirectory, "deck.json");

        IReadOnlyList<WordEntry> loadedWords = JsonDataLoader.LoadWords(
            wordsJsonPath,
            wordsTextPath,
            gaccagWordsPath);
        IReadOnlyList<CharacterEntry> loadedDeck = JsonDataLoader.LoadCharacters(deckPath);
        _modeShiftLibraryCache = null;

        if (loadedWords.Count == 0)
        {
            throw new InvalidOperationException(
                "words.json 또는 words.txt에 단어가 없습니다.");
        }

        if (loadedDeck.Count == 0)
        {
            throw new InvalidOperationException("deck.json에 캐릭터가 없습니다.");
        }

        var validCharacterIds = loadedDeck
            .Select(character => character.Id)
            .ToHashSet(StringComparer.Ordinal);
        _selectedHandCharacterIds.RemoveAll(id => !validCharacterIds.Contains(id));
        foreach (string characterId in _selectedHandLetterStateIds.Keys.ToArray())
        {
            CharacterEntry? character = loadedDeck.FirstOrDefault(item => item.Id == characterId);
            string stateId = _selectedHandLetterStateIds[characterId];
            if (!_selectedHandCharacterIds.Contains(characterId, StringComparer.Ordinal) ||
                character is null ||
                character.FindLetterState(stateId) is null)
            {
                _selectedHandLetterStateIds.Remove(characterId);
            }
        }
        foreach (string characterId in _selectedHandFormIds.Keys.ToArray())
        {
            CharacterEntry? character = loadedDeck.FirstOrDefault(item => item.Id == characterId);
            string formId = _selectedHandFormIds[characterId];
            if (!_selectedHandCharacterIds.Contains(characterId, StringComparer.Ordinal) ||
                character is null ||
                character.FindForm(formId) is null)
            {
                _selectedHandFormIds.Remove(characterId);
            }
        }

        _deck = loadedDeck;
        RebuildDeckLookups();

        _loadedWordCount = loadedWords.Count;
        _loadedSearchWordCount = 0;
        _loadedShortWordCount = 0;
        _loadedGaccagWordCount = 0;
        _loadedGaccagSearchWordCount = 0;
        _loadedGaccagShortWordCount = 0;
        _loadedGaccagFourLetterCount = 0;
        _loadedGaccagFiveLetterCount = 0;
        _loadedGaccagSixLetterCount = 0;
        _loadedGaccagSevenLetterCount = 0;

        // 통계 때문에 21만+ 단어를 여러 번 재분해하던 비용을 없애고 한 번만 순회합니다.
        foreach (WordEntry word in loadedWords)
        {
            int cellCount = GetWordCellCount(word);
            if (cellCount is >= 4 and <= 7)
            {
                _loadedSearchWordCount++;
            }
            else if (cellCount is >= 2 and <= 3)
            {
                _loadedShortWordCount++;
            }

            if (!string.Equals(word.Source, "GACCAG", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _loadedGaccagWordCount++;
            if (cellCount is >= 4 and <= 7)
            {
                _loadedGaccagSearchWordCount++;
            }
            else if (cellCount is >= 2 and <= 3)
            {
                _loadedGaccagShortWordCount++;
            }

            switch (cellCount)
            {
                case 4: _loadedGaccagFourLetterCount++; break;
                case 5: _loadedGaccagFiveLetterCount++; break;
                case 6: _loadedGaccagSixLetterCount++; break;
                case 7: _loadedGaccagSevenLetterCount++; break;
            }
        }
        string comboCheckpointPath = Path.Combine(
            _dataDirectory,
            $"gaccag_checkpoint_{GaccagImportMode.ComboWords}.json");
        _hasCompleteComboData = _loadedShortWordCount > 0
                                && !File.Exists(comboCheckpointPath);
        _searchService = new WordSearchService(loadedWords);
        _lastGeneralSearchSignature = string.Empty;
        _lastHandSearchSignature = string.Empty;
        _lastDeckSearchSignature = string.Empty;

        BuildCharacterButtons();
        RefreshMainPresetList();
        UpdateMiracleLeaderStatus();
        DataStatsText.Text = _loadedGaccagWordCount > 0
            ? $"GACCAG 4글자 {_loadedGaccagFourLetterCount:N0} · 5글자 {_loadedGaccagFiveLetterCount:N0} · " +
              $"6글자 {_loadedGaccagSixLetterCount:N0} · 7글자 {_loadedGaccagSevenLetterCount:N0} · " +
              (_loadedShortWordCount > 0
                  ? (_hasCompleteComboData
                      ? $"콤보용 2~3글자 {_loadedShortWordCount:N0} · 전체 {_loadedWordCount:N0}개"
                      : $"콤보용 2~3글자 {_loadedShortWordCount:N0}(수집 중) · 전체 {_loadedWordCount:N0}개")
                  : $"콤보용 2~3글자 미수집 · 전체 {_loadedWordCount:N0}개")
            : $"단어 {_loadedWordCount:N0}개 · 덱 캐릭터 {_deck.Count:N0}명 · GACCAG 데이터 미설치";
        UpdateDictionaryInfoText();
    }

    private void RebuildDeckLookups()
    {
        _deckById.Clear();
        _deckIndexById.Clear();

        for (int index = 0; index < _deck.Count; index++)
        {
            CharacterEntry character = _deck[index];
            _deckById[character.Id] = character;
            _deckIndexById[character.Id] = index;
        }
    }


    /// <summary>
    /// 덱 편집기에서 돌아올 때는 단어 사전이 바뀌지 않았으므로
    /// 21만+ 단어와 WordSearchService를 다시 만들지 않고 덱 12명만 갱신합니다.
    /// </summary>
    private void ReloadDeckOnlyFromDisk()
    {
        string deckPath = Path.Combine(_dataDirectory, "deck.json");
        IReadOnlyList<CharacterEntry> loadedDeck = JsonDataLoader.LoadCharacters(deckPath);
        if (loadedDeck.Count == 0)
        {
            throw new InvalidOperationException("deck.json에 캐릭터가 없습니다.");
        }

        var validCharacterIds = loadedDeck
            .Select(character => character.Id)
            .ToHashSet(StringComparer.Ordinal);
        _selectedHandCharacterIds.RemoveAll(id => !validCharacterIds.Contains(id));

        var loadedById = loadedDeck
            .Where(character => !string.IsNullOrWhiteSpace(character.Id))
            .ToDictionary(character => character.Id, StringComparer.Ordinal);

        foreach (string characterId in _selectedHandLetterStateIds.Keys.ToArray())
        {
            _selectedHandLetterStateIds.TryGetValue(characterId, out string? stateId);
            if (!_selectedHandCharacterIds.Contains(characterId, StringComparer.Ordinal) ||
                !loadedById.TryGetValue(characterId, out CharacterEntry? character) ||
                character.FindLetterState(stateId) is null)
            {
                _selectedHandLetterStateIds.Remove(characterId);
            }
        }

        foreach (string characterId in _selectedHandFormIds.Keys.ToArray())
        {
            _selectedHandFormIds.TryGetValue(characterId, out string? formId);
            if (!_selectedHandCharacterIds.Contains(characterId, StringComparer.Ordinal) ||
                !loadedById.TryGetValue(characterId, out CharacterEntry? character) ||
                character.FindForm(formId) is null)
            {
                _selectedHandFormIds.Remove(characterId);
            }
        }

        _deck = loadedDeck;
        RebuildDeckLookups();

        // 단어 인덱스는 그대로 재사용하고 덱/손패 관련 캐시만 무효화합니다.
        _lastHandSearchSignature = string.Empty;
        _lastDeckSearchSignature = string.Empty;

        BuildCharacterButtons();
        RefreshMainPresetList();
        UpdateMiracleLeaderStatus();
    }

    private static int GetWordCellCount(WordEntry word)
        => word.Cells is { Count: > 0 }
            ? word.Cells.Count
            : KanaUtility.SplitIntoCells(word.Text).Count;

    private string GetExistingGaccagWordsPath()
    {
        string gzipPath = Path.Combine(_dataDirectory, "gaccag_words.json.gz");
        if (File.Exists(gzipPath))
        {
            return gzipPath;
        }

        return Path.Combine(_dataDirectory, "gaccag_words.json");
    }

    private void MigrateGaccagJsonToGzipSafely()
    {
        string sourcePath = Path.Combine(_dataDirectory, "gaccag_words.json");
        string destinationPath = Path.Combine(_dataDirectory, "gaccag_words.json.gz");
        if (!File.Exists(sourcePath) || File.Exists(destinationPath))
        {
            return;
        }

        string temporaryPath = destinationPath + ".tmp";
        try
        {
            using (FileStream input = File.OpenRead(sourcePath))
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            {
                input.CopyTo(gzip);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            File.Delete(sourcePath);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // 용량 최적화 실패는 프로그램 실행에 영향을 주지 않습니다.
            }
        }
    }

    private void BuildBoardButtons()
    {
        BoardPanel.Children.Clear();
        _boardButtons.Clear();

        for (int index = 0; index < BoardSize; index++)
        {
            int capturedIndex = index;
            var button = new Button
            {
                Content = "□",
                Height = 72,
                Margin = new Thickness(5),
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = BrushFromHex("#222A37"),
                BorderBrush = BrushFromHex("#3A4658"),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                ToolTip = $"{index + 1}번 칸"
            };

            button.Click += (_, _) =>
            {
                _activeBoardIndex = capturedIndex;
                UpdateBoardButtons();
            };

            _boardButtons.Add(button);
            BoardPanel.Children.Add(button);
        }
    }

    private void BuildKanaSection(
        Panel panel,
        string sectionTitle,
        IEnumerable<KanaRow> rows,
        bool includeBlankButton = false)
    {
        panel.Children.Clear();

        panel.Children.Add(new TextBlock
        {
            Text = sectionTitle,
            Foreground = BrushFromHex("#66D9EF"),
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Margin = new Thickness(2, 0, 0, 6)
        });

        var rowsPanel = new WrapPanel();

        if (includeBlankButton)
        {
            var blankButtons = new WrapPanel();
            blankButtons.Children.Add(CreateKanaButton("빈칸", null, 76));
            rowsPanel.Children.Add(CreateKanaRowCard("빈칸", blankButtons));
        }

        foreach (KanaRow row in rows)
        {
            var buttons = new WrapPanel();
            foreach (string letter in row.Letters)
            {
                buttons.Children.Add(CreateKanaButton(letter, letter, 42));
            }

            rowsPanel.Children.Add(CreateKanaRowCard(row.Label, buttons));
        }

        panel.Children.Add(rowsPanel);
    }

    private Border CreateKanaRowCard(string label, UIElement buttons)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = BrushFromHex("#8FA0B4"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 0, 3)
        });
        content.Children.Add(buttons);

        return new Border
        {
            Width = 260,
            MinHeight = 66,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(5, 5, 5, 4),
            Background = BrushFromHex("#1D2430"),
            BorderBrush = BrushFromHex("#303C4F"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content
        };
    }

    private Button CreateKanaButton(string label, string? value, double width)
    {
        var button = new Button
        {
            Content = label,
            Width = width,
            Height = 38,
            Margin = new Thickness(3),
            FontSize = value is null ? 12 : 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Background = value is null
                ? BrushFromHex("#5A3440")
                : BrushFromHex("#2A3342"),
            BorderBrush = BrushFromHex("#46546A"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand
        };

        button.Click += (_, _) => SetActiveBoardCell(value, advance: true);
        return button;
    }

    private void BuildCharacterButtons()
    {
        DeckCharacterPanel.Children.Clear();
        _characterButtons.Clear();

        for (int deckIndex = 0; deckIndex < _deck.Count; deckIndex++)
        {
            CharacterEntry character = _deck[deckIndex];
            bool isLeader = deckIndex == 0;
            var button = new Button
            {
                Width = 205,
                Height = 78,
                Margin = new Thickness(4),
                Padding = new Thickness(7),
                Foreground = Brushes.White,
                Background = BrushFromHex("#222A37"),
                BorderBrush = BrushFromHex("#3A4658"),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                ToolTip = BuildCharacterToolTip(character, isLeader)
            };

            string capturedId = character.Id;
            button.Click += (_, _) => ToggleHandCharacter(capturedId);

            _characterButtons[character.Id] = button;
            DeckCharacterPanel.Children.Add(button);
        }

        // BuildCharacterButtons는 모드시프트처럼 런타임에 덱 캐릭터 ID가 바뀔 때도 호출됩니다.
        // 버튼 틀만 다시 만들고 Content를 채우지 않으면 카드가 빈 회색 상자로 보이다가
        // 클릭으로 해당 버튼이 갱신될 때만 다시 나타나는 현상이 생깁니다.
        UpdateCharacterButtons();
        DeckCharacterPanel.InvalidateVisual();
    }

    private void RestoreSettings()
    {
        UserSettings settings = UserSettingsService.Load();

        for (int index = 0; index < Math.Min(BoardSize, settings.BoardCells.Count); index++)
        {
            string? value = settings.BoardCells[index];
            _boardCells[index] = string.IsNullOrWhiteSpace(value)
                ? null
                : KanaUtility.NormalizeCell(value);
        }

        foreach (string id in settings.SelectedHandCharacterIds)
        {
            if (_selectedHandCharacterIds.Count >= HandSize)
            {
                break;
            }

            if (_deck.Any(character => character.Id == id) &&
                !_selectedHandCharacterIds.Contains(id, StringComparer.Ordinal))
            {
                _selectedHandCharacterIds.Add(id);
            }
        }

        _selectedHandLetterStateIds.Clear();
        Dictionary<string, string> savedStates = settings.SelectedHandLetterStateIds
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string characterId, string stateId) in savedStates)
        {
            CharacterEntry? character = _deck.FirstOrDefault(item => item.Id == characterId);
            if (character is not null && character.FindLetterState(stateId) is not null)
            {
                _selectedHandLetterStateIds[characterId] = stateId;
            }
        }

        _selectedHandFormIds.Clear();
        Dictionary<string, string> savedForms = settings.SelectedHandFormIds
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string characterId, string formId) in savedForms)
        {
            CharacterEntry? character = _deck.FirstOrDefault(item => item.Id == characterId);
            if (character is not null && character.FindForm(formId) is not null)
            {
                _selectedHandFormIds[characterId] = formId;
            }
        }

        AutoSearchCheckBox.IsChecked = settings.AutoSearchEnabled;
        _deckResultSortMode = NormalizeDeckResultSortMode(settings.DeckResultSortMode);
        DeckResultSortComboBox.SelectedItem = DeckResultSortComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), _deckResultSortMode, StringComparison.Ordinal))
            ?? DeckResultSortComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void SaveSettings()
    {
        if (_isInitializing)
        {
            return;
        }

        UserSettings previousSettings = UserSettingsService.Load();
        var settings = new UserSettings
        {
            BoardCells = _boardCells.ToList(),
            SelectedHandCharacterIds = _selectedHandCharacterIds.ToList(),
            SelectedHandLetterStateIds = _selectedHandLetterStateIds
                .Where(pair => _selectedHandCharacterIds.Contains(pair.Key, StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            SelectedHandFormIds = _selectedHandFormIds
                .Where(pair => _selectedHandCharacterIds.Contains(pair.Key, StringComparer.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            AutoSearchEnabled = AutoSearchCheckBox.IsChecked == true,
            DeckResultSortMode = _deckResultSortMode,
            LastDeckEditorCharacterId = previousSettings.LastDeckEditorCharacterId,
            LastDeckEditorSearchText = previousSettings.LastDeckEditorSearchText,
            LastDeckEditorGroupFilter = previousSettings.LastDeckEditorGroupFilter,
            LastDeckEditorCategoryFilter = previousSettings.LastDeckEditorCategoryFilter,
            LastDeckEditorStatusFilter = previousSettings.LastDeckEditorStatusFilter,
            LastDeckEditorSortMode = previousSettings.LastDeckEditorSortMode,
            LastDeckEditorFavoritesOnly = previousSettings.LastDeckEditorFavoritesOnly,
            LastDeckEditorBelovedOnly = previousSettings.LastDeckEditorBelovedOnly
        };

        UserSettingsService.Save(settings);
    }

    private void UpdateBoardButtons()
    {
        for (int index = 0; index < _boardButtons.Count; index++)
        {
            Button button = _boardButtons[index];
            button.Content = _boardCells[index] ?? "□";

            bool isActive = index == _activeBoardIndex;
            button.BorderBrush = isActive
                ? BrushFromHex("#66D9EF")
                : BrushFromHex("#3A4658");
            button.Background = isActive
                ? BrushFromHex("#263C4A")
                : BrushFromHex("#222A37");
        }

        ActiveSlotText.Text = $"{_activeBoardIndex + 1}번 칸 선택 중";
        QuickBoardInputTextBox.Text = string.Concat(
            _boardCells.Select(cell => cell ?? "_"));
    }

    private FrameworkElement CreateCharacterImageElement(CharacterEntry character, double size)
    {
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(7),
            Background = BrushFromHex("#18202B"),
            BorderBrush = BrushFromHex("#46546A"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center
        };

        var bitmap = CharacterImageService.LoadBitmap(
            _dataDirectory,
            character.GetActiveImageFileName(),
            Math.Max(64, (int)Math.Ceiling(size * 2)));

        if (bitmap is not null)
        {
            border.Child = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else
        {
            string fallback = string.IsNullOrWhiteSpace(character.Name)
                ? "?"
                : character.Name[..1];
            border.Child = new TextBlock
            {
                Text = fallback,
                Foreground = BrushFromHex("#8FA0B4"),
                FontSize = Math.Max(15, size * 0.33),
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return border;
    }

    private void UpdateCharacterButtons()
    {
        foreach (CharacterEntry character in _deck)
        {
            UpdateCharacterButton(character.Id);
        }

        UpdateHandCountText();
    }

    private void UpdateCharacterButtons(IEnumerable<string> characterIds)
    {
        foreach (string characterId in characterIds.Distinct(StringComparer.Ordinal))
        {
            UpdateCharacterButton(characterId);
        }

        UpdateHandCountText();
    }

    private void UpdateHandCountText()
        => HandCountText.Text = $"{_selectedHandCharacterIds.Count} / {HandSize} 선택";

    private void UpdateCharacterButton(string characterId)
    {
        if (!_deckById.TryGetValue(characterId, out CharacterEntry? character) ||
            !_deckIndexById.TryGetValue(characterId, out int deckIndex) ||
            !_characterButtons.TryGetValue(characterId, out Button? button))
        {
            return;
        }

        bool isLeader = deckIndex == 0;
        int selectedIndex = _selectedHandCharacterIds.FindIndex(
            id => string.Equals(id, character.Id, StringComparison.Ordinal));
        bool isSelected = selectedIndex >= 0;
        string selectedStateId = isSelected
            ? GetSelectedHandLetterStateId(character)
            : CharacterEntry.BaseLetterStateId;
        string selectedFormId = isSelected
            ? GetSelectedHandFormId(character)
            : character.HasAlternateForms
                ? CharacterEntry.AllFormsId
                : CharacterEntry.BaseFormId;
        CharacterLetterState? selectedState = character.FindLetterState(selectedStateId);
        CharacterForm? selectedForm = character.FindForm(selectedFormId);
        CharacterEntry displayCharacter = CreateDisplayCharacter(
            character,
            selectedStateId,
            selectedFormId);
        string stateSuffix = selectedState is null
            ? string.Empty
            : $" · {selectedState.Name}";
        string formSuffix = selectedForm is null
            ? character.HasAlternateForms ? " · MS" : string.Empty
            : $" · 〔{selectedForm.Name}〕";
        string miracleSuffix = displayCharacter.HasActiveMiracleGrant
            ? " · ✨리더 문자"
            : string.Empty;
        string deckGroupSuffix = displayCharacter.HasActiveDeckGroupGrant
            ? " · ◆덱 조건 문자"
            : string.Empty;

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement imageElement = CreateCharacterImageElement(displayCharacter, 52);
        imageElement.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(imageElement, 0);
        content.Children.Add(imageElement);

        var textPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        textPanel.Children.Add(new TextBlock
        {
            Text = isSelected
                ? $"{selectedIndex + 1}. {(isLeader ? "[리더] " : string.Empty)}{character.Name}{formSuffix}{stateSuffix}{miracleSuffix}{deckGroupSuffix}"
                : $"{(isLeader ? "[리더] " : string.Empty)}{character.Name}{formSuffix}{miracleSuffix}{deckGroupSuffix}",
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", displayCharacter.GetAvailableLetters(selectedStateId)),
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = isSelected
                ? BrushFromHex("#DFF8FF")
                : BrushFromHex("#AEB8C8"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(textPanel, 1);
        content.Children.Add(textPanel);

        button.Content = content;
        button.ToolTip = BuildCharacterToolTip(displayCharacter, isLeader);
        button.Background = isSelected
            ? BrushFromHex("#286C86")
            : BrushFromHex("#222A37");
        button.BorderBrush = isSelected
            ? BrushFromHex("#66D9EF")
            : BrushFromHex("#3A4658");
        button.BorderThickness = new Thickness(isSelected ? 2 : 1);
    }

    private void RenderSelectedHandSlots()
    {
        CancelHandModeShiftHold();
        _isRenderingSelectedHand = true;
        try
        {
            SelectedHandPanel.Children.Clear();

            for (int index = 0; index < HandSize; index++)
            {
                if (index >= _selectedHandCharacterIds.Count)
                {
                    var emptyContent = new StackPanel
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    emptyContent.Children.Add(new TextBlock
                    {
                        Text = $"{index + 1}",
                        FontSize = 22,
                        FontWeight = FontWeights.Bold,
                        Foreground = BrushFromHex("#46566A"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                    emptyContent.Children.Add(new TextBlock
                    {
                        Text = "미선택",
                        Margin = new Thickness(0, 3, 0, 0),
                        Foreground = BrushFromHex("#7F8999"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                    emptyContent.Children.Add(new TextBlock
                    {
                        Text = "덱 캐릭터를 클릭해 손패에 추가",
                        Margin = new Thickness(0, 5, 0, 0),
                        FontSize = 10,
                        Foreground = BrushFromHex("#59687B"),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });

                    SelectedHandPanel.Children.Add(new Border
                    {
                        Height = 142,
                        Margin = new Thickness(4),
                        Padding = new Thickness(8),
                        CornerRadius = new CornerRadius(10),
                        BorderBrush = BrushFromHex("#334154"),
                        BorderThickness = new Thickness(1),
                        Background = BrushFromHex("#171E29"),
                        Child = emptyContent
                    });
                    continue;
                }

                string characterId = _selectedHandCharacterIds[index];
                if (!_deckById.TryGetValue(characterId, out CharacterEntry? character))
                {
                    // 덱 저장 직후 연결형 모드시프트 치환 중 아주 짧게 오래된 ID가 남아도
                    // UI 렌더링이 중단되지 않도록 다음 갱신에서 정리합니다.
                    continue;
                }

                int capturedIndex = index;

                FormOptionItem[] formOptions = CreateFormOptions(character);
                string selectedFormId = GetSelectedHandFormId(character);
                FormOptionItem? selectedFormOption = formOptions.FirstOrDefault(item =>
                    string.Equals(item.Id, selectedFormId, StringComparison.Ordinal));
                string effectiveFormId = selectedFormOption?.Id ?? CharacterEntry.BaseFormId;

                LetterStateOptionItem[] stateOptions = CreateLetterStateOptions(character);
                string selectedStateId = GetSelectedHandLetterStateId(character);
                LetterStateOptionItem? selectedStateOption = stateOptions.FirstOrDefault(item =>
                    string.Equals(item.Id, selectedStateId, StringComparison.Ordinal));
                string effectiveStateId = selectedStateOption?.Id ?? CharacterEntry.BaseLetterStateId;

                CharacterEntry displayCharacter = CreateDisplayCharacter(
                    character,
                    effectiveStateId,
                    effectiveFormId);
                CharacterForm? selectedForm = character.FindForm(effectiveFormId);
                IReadOnlyList<string> activeAttributes = GetDisplayAttributes(displayCharacter);
                string activeSpecies = GetDisplaySpecies(displayCharacter);
                Brush accentBrush = CreateAttributeAccentBrush(activeAttributes);
                bool hasConnectedModeShift = !string.IsNullOrWhiteSpace(character.DeckRestrictionGroupId);
                bool hasSameNameModeShift = character.HasAlternateForms;
                bool hasModeShift = hasConnectedModeShift || hasSameNameModeShift;

                // 동일명 모드시프트는 이미지 2.2초 롱프레스로만 전환합니다.
                // 손패 카드 안의 형태 ComboBox를 제거해 조작 방식을 하나로 통일합니다.
                int selectorCount = stateOptions.Length > 0 ? 1 : 0;
                double cardHeight = 142 + selectorCount * 34;

                var card = new Border
                {
                    Height = cardHeight,
                    Margin = new Thickness(4),
                    Padding = new Thickness(9, 8, 9, 8),
                    Background = BrushFromHex("#202B38"),
                    BorderBrush = accentBrush,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(10),
                    ToolTip = hasModeShift
                        ? "MS 캐릭터 · 이미지를 2.2초 길게 눌러 모드시프트 전환"
                        : null
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int row = 0; row < selectorCount; row++)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                }
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                FrameworkElement imageElement = CreateCharacterImageElement(displayCharacter, 68);
                imageElement.HorizontalAlignment = HorizontalAlignment.Center;
                imageElement.VerticalAlignment = VerticalAlignment.Center;

                var imageGrid = new Grid
                {
                    Width = 76,
                    Height = 80,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                var imageFrame = new Border
                {
                    Width = 72,
                    Height = 72,
                    Padding = new Thickness(1),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = BrushFromHex("#151C26"),
                    BorderBrush = accentBrush,
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(9),
                    Child = imageElement,
                    Cursor = hasModeShift ? Cursors.Hand : Cursors.Arrow,
                    ToolTip = hasConnectedModeShift
                        ? "2.2초 길게 눌러 연결형 모드시프트 전환 · 덱 슬롯과 손패가 함께 바뀝니다."
                        : hasSameNameModeShift
                            ? "2.2초 길게 눌러 동일명 모드시프트 형태 전환"
                            : null
                };
                imageGrid.Children.Add(imageFrame);

                var holdProgress = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = 0,
                    Height = 4,
                    Width = 68,
                    Margin = new Thickness(2, 0, 0, 1),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Foreground = accentBrush,
                    Background = BrushFromHex("#313B49"),
                    BorderThickness = new Thickness(0),
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false
                };
                imageGrid.Children.Add(holdProgress);

                if (hasModeShift)
                {
                    var msBadge = new Border
                    {
                        Background = BrushFromHex("#392E52"),
                        BorderBrush = BrushFromHex("#A88AE8"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(4, 1, 4, 1),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 3, 1, 0),
                        IsHitTestVisible = false,
                        Child = new TextBlock
                        {
                            Text = hasConnectedModeShift ? "MS↔" : "MS",
                            FontSize = 9,
                            FontWeight = FontWeights.Bold,
                            Foreground = BrushFromHex("#E4D4FF")
                        }
                    };
                    imageGrid.Children.Add(msBadge);

                    imageFrame.PreviewMouseLeftButtonDown += (_, e) =>
                    {
                        StartHandModeShiftHold(capturedIndex, holdProgress, imageFrame);
                        e.Handled = true;
                    };
                    imageFrame.PreviewMouseLeftButtonUp += (_, e) =>
                    {
                        CancelHandModeShiftHold();
                        e.Handled = true;
                    };
                }

                Grid.SetRow(imageGrid, 0);
                Grid.SetRowSpan(imageGrid, grid.RowDefinitions.Count);
                Grid.SetColumn(imageGrid, 0);
                grid.Children.Add(imageGrid);

                var titlePanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                titlePanel.Children.Add(new Border
                {
                    Background = index == 0 ? BrushFromHex("#4A4025") : BrushFromHex("#233446"),
                    BorderBrush = index == 0 ? BrushFromHex("#C8A84B") : BrushFromHex("#3E607A"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(0, 0, 6, 0),
                    Child = new TextBlock
                    {
                        Text = index == 0 ? "HAND 1" : $"HAND {index + 1}",
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        Foreground = index == 0 ? BrushFromHex("#FFE099") : BrushFromHex("#9DDCF3")
                    }
                });
                titlePanel.Children.Add(new TextBlock
                {
                    Text = selectedForm is null
                        ? character.Name
                        : $"{character.Name}〔{selectedForm.Name}〕",
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    Foreground = BrushFromHex("#F0F5FA"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxWidth = 170
                });
                Grid.SetRow(titlePanel, 0);
                Grid.SetColumn(titlePanel, 1);
                grid.Children.Add(titlePanel);

                var removeButton = new Button
                {
                    Content = "×",
                    Width = 25,
                    Height = 23,
                    Padding = new Thickness(0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Background = BrushFromHex("#4A2932"),
                    Foreground = BrushFromHex("#FFD8DF"),
                    BorderBrush = BrushFromHex("#75404D"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    ToolTip = "손패에서 해제"
                };
                removeButton.Click += (_, _) => RemoveHandCharacterAt(capturedIndex);
                Grid.SetRow(removeButton, 0);
                Grid.SetColumn(removeButton, 2);
                grid.Children.Add(removeButton);

                var metaPanel = new WrapPanel
                {
                    Margin = new Thickness(0, 5, 0, 0)
                };
                foreach (string attribute in activeAttributes)
                {
                    metaPanel.Children.Add(CreateHandMetaBadge(
                        attribute,
                        GetAttributeColorHex(attribute),
                        GetAttributeColorHex(attribute)));
                }
                if (!string.IsNullOrWhiteSpace(activeSpecies))
                {
                    metaPanel.Children.Add(CreateHandMetaBadge(
                        $"{activeSpecies} 종족",
                        "#66758B",
                        "#CBD6E5"));
                }
                if (displayCharacter.HasActiveMiracleGrant)
                {
                    metaPanel.Children.Add(CreateHandMetaBadge("리더 부여", "#9A7BD4", "#E4D4FF"));
                }
                else if (displayCharacter.HasActiveDeckGroupGrant)
                {
                    metaPanel.Children.Add(CreateHandMetaBadge("그룹 부여", "#4E9B82", "#BFF5DD"));
                }
                Grid.SetRow(metaPanel, 1);
                Grid.SetColumn(metaPanel, 1);
                Grid.SetColumnSpan(metaPanel, 2);
                grid.Children.Add(metaPanel);

                int nextRow = 2;
                if (stateOptions.Length > 0)
                {
                    var statePanel = new Grid
                    {
                        Margin = new Thickness(0, 5, 0, 0)
                    };
                    statePanel.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star)
                    });
                    statePanel.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = GridLength.Auto
                    });

                    var stateComboBox = new ComboBox
                    {
                        ItemsSource = stateOptions,
                        SelectedItem = selectedStateOption,
                        DisplayMemberPath = nameof(LetterStateOptionItem.DisplayText),
                        MinHeight = 29,
                        Padding = new Thickness(7, 3, 7, 3),
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        IsEditable = false,
                        ToolTip = "현재 전투에서 활성화된 조건부·변신 문자 상태를 선택하세요."
                    };
                    stateComboBox.SelectionChanged += (_, _) =>
                    {
                        if (_isRenderingSelectedHand ||
                            stateComboBox.SelectedItem is not LetterStateOptionItem option)
                        {
                            return;
                        }

                        SetSelectedHandLetterState(character.Id, option.Id);
                    };
                    var stateComboContainer = new Grid();
                    stateComboContainer.Children.Add(stateComboBox);
                    if (selectedStateOption is null)
                    {
                        stateComboContainer.Children.Add(new TextBlock
                        {
                            Text = "조건 문자 선택...",
                            Foreground = BrushFromHex("#AEB8C8"),
                            Margin = new Thickness(10, 0, 28, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            IsHitTestVisible = false,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                    }
                    Grid.SetColumn(stateComboContainer, 0);
                    statePanel.Children.Add(stateComboContainer);

                    var clearStateButton = new Button
                    {
                        Content = "조건 해제",
                        Margin = new Thickness(6, 0, 0, 0),
                        Padding = new Thickness(7, 2, 7, 2),
                        MinHeight = 27,
                        Background = BrushFromHex("#3A3440"),
                        Foreground = BrushFromHex("#E6DDF0"),
                        BorderBrush = BrushFromHex("#5A4F66"),
                        BorderThickness = new Thickness(1),
                        Cursor = Cursors.Hand,
                        Visibility = selectedStateOption is null
                            ? Visibility.Collapsed
                            : Visibility.Visible,
                        ToolTip = "조건 문자를 해제하고 현재 형태의 기본 문자만 사용"
                    };
                    clearStateButton.Click += (_, _) => SetSelectedHandLetterState(
                        character.Id,
                        CharacterEntry.BaseLetterStateId);
                    Grid.SetColumn(clearStateButton, 1);
                    statePanel.Children.Add(clearStateButton);

                    Grid.SetRow(statePanel, nextRow++);
                    Grid.SetColumn(statePanel, 1);
                    Grid.SetColumnSpan(statePanel, 2);
                    grid.Children.Add(statePanel);
                }

                var lettersBadge = new Border
                {
                    Margin = new Thickness(0, 6, 0, 0),
                    Padding = new Thickness(7, 4, 7, 4),
                    Background = BrushFromHex("#17212C"),
                    BorderBrush = BrushFromHex("#32465A"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Child = new TextBlock
                    {
                        Text = string.Join(" · ", displayCharacter.GetAvailableLetters(effectiveStateId)),
                        Foreground = displayCharacter.HasActiveMiracleGrant
                            ? BrushFromHex("#D9C2FF")
                            : displayCharacter.HasActiveDeckGroupGrant
                                ? BrushFromHex("#8FE3B1")
                                : selectedStateOption is not null
                                    ? BrushFromHex("#FFD08A")
                                    : selectedForm is not null
                                        ? BrushFromHex("#8FE3B1")
                                        : BrushFromHex("#B8EAF5"),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                Grid.SetRow(lettersBadge, nextRow);
                Grid.SetColumn(lettersBadge, 1);
                Grid.SetColumnSpan(lettersBadge, 2);
                grid.Children.Add(lettersBadge);

                card.Child = grid;
                SelectedHandPanel.Children.Add(card);
            }
        }
        finally
        {
            _isRenderingSelectedHand = false;
        }
    }

    private static Border CreateHandMetaBadge(string text, string borderColor, string foregroundColor)
        => new()
        {
            Margin = new Thickness(0, 0, 5, 2),
            Padding = new Thickness(5, 1, 5, 1),
            Background = BrushFromHex("#17212C"),
            BorderBrush = BrushFromHex(borderColor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = BrushFromHex(foregroundColor)
            }
        };

    private static IReadOnlyList<string> GetDisplayAttributes(CharacterEntry character)
    {
        CharacterForm? form = character.FindForm(character.ActiveFormId);
        string main = DeckDataService.NormalizeAttribute(form?.Attribute);
        if (main.Length == 0)
        {
            main = DeckDataService.NormalizeAttribute(character.Attribute);
        }

        var values = new List<string>();
        if (main.Length > 0)
        {
            values.Add(main);
        }

        IEnumerable<string> subAttributes = form is not null &&
                                             (!string.IsNullOrWhiteSpace(form.Attribute) ||
                                              (form.SubAttributes?.Count ?? 0) > 0)
            ? form.SubAttributes ?? new List<string>()
            : character.SubAttributes ?? new List<string>();
        foreach (string value in DeckDataService.NormalizeAttributes(subAttributes, main))
        {
            if (!values.Contains(value, StringComparer.Ordinal))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string GetDisplaySpecies(CharacterEntry character)
    {
        CharacterForm? form = character.FindForm(character.ActiveFormId);
        string species = DeckDataService.NormalizeSpecies(form?.Species);
        return species.Length > 0
            ? species
            : DeckDataService.NormalizeSpecies(character.Species);
    }

    private static string GetAttributeColorHex(string attribute)
        => attribute switch
        {
            "火" => "#FF6B6B",
            "水" => "#63B3FF",
            "木" => "#70D98B",
            "光" => "#FFE27A",
            "闇" => "#B995FF",
            "天" => "#8FE9FF",
            "冥" => "#C58A5A",
            "虹" => "#FF9EDB",
            _ => "#66D9EF"
        };

    private static Brush CreateAttributeAccentBrush(IReadOnlyList<string> attributes)
    {
        if (attributes.Count == 0)
        {
            return BrushFromHex("#4B6578");
        }

        Color first = (Color)ColorConverter.ConvertFromString(GetAttributeColorHex(attributes[0]));
        if (attributes.Count == 1)
        {
            return new SolidColorBrush(first);
        }

        Color second = (Color)ColorConverter.ConvertFromString(GetAttributeColorHex(attributes[1]));
        var brush = new LinearGradientBrush(first, second, 0);
        brush.Freeze();
        return brush;
    }

    private void StartHandModeShiftHold(int handIndex, ProgressBar progress, Border imageFrame)
    {
        CancelHandModeShiftHold();
        if (handIndex < 0 || handIndex >= _selectedHandCharacterIds.Count)
        {
            return;
        }

        string characterId = _selectedHandCharacterIds[handIndex];
        if (!_deckById.TryGetValue(characterId, out CharacterEntry? character) ||
            (string.IsNullOrWhiteSpace(character.DeckRestrictionGroupId) && !character.HasAlternateForms))
        {
            return;
        }

        _pendingHandModeShiftIndex = handIndex;
        _handModeShiftHoldStartedUtc = DateTime.UtcNow;
        _pendingHandModeShiftProgress = progress;
        _pendingHandModeShiftFrame = imageFrame;
        progress.Value = 0;
        progress.Visibility = Visibility.Visible;
        imageFrame.Opacity = 0.88;
        _handModeShiftHoldTimer.Start();
    }

    private void HandModeShiftHoldTimer_Tick(object? sender, EventArgs e)
    {
        if (_pendingHandModeShiftIndex < 0 || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            CancelHandModeShiftHold();
            return;
        }

        double elapsedMs = (DateTime.UtcNow - _handModeShiftHoldStartedUtc).TotalMilliseconds;
        if (_pendingHandModeShiftProgress is not null)
        {
            _pendingHandModeShiftProgress.Value = Math.Clamp(
                elapsedMs / HandModeShiftHoldDurationMs * 100.0,
                0,
                100);
        }

        if (elapsedMs < HandModeShiftHoldDurationMs)
        {
            return;
        }

        int handIndex = _pendingHandModeShiftIndex;
        _handModeShiftHoldTimer.Stop();
        if (_pendingHandModeShiftProgress is not null)
        {
            _pendingHandModeShiftProgress.Value = 100;
        }
        if (_pendingHandModeShiftFrame is not null)
        {
            _pendingHandModeShiftFrame.Opacity = 1.0;
        }
        _pendingHandModeShiftIndex = -1;
        _pendingHandModeShiftProgress = null;
        _pendingHandModeShiftFrame = null;

        PerformHandModeShift(handIndex);
    }

    private void CancelHandModeShiftHold()
    {
        _handModeShiftHoldTimer.Stop();
        if (_pendingHandModeShiftProgress is not null)
        {
            _pendingHandModeShiftProgress.Value = 0;
            _pendingHandModeShiftProgress.Visibility = Visibility.Collapsed;
        }
        if (_pendingHandModeShiftFrame is not null)
        {
            _pendingHandModeShiftFrame.Opacity = 1.0;
        }

        _pendingHandModeShiftIndex = -1;
        _pendingHandModeShiftProgress = null;
        _pendingHandModeShiftFrame = null;
    }

    private void PerformHandModeShift(int handIndex)
    {
        if (handIndex < 0 || handIndex >= _selectedHandCharacterIds.Count)
        {
            return;
        }

        string characterId = _selectedHandCharacterIds[handIndex];
        if (!_deckById.TryGetValue(characterId, out CharacterEntry? character))
        {
            return;
        }

        // 덱 편집기와 동일하게 이름이 다른 연결형 모드시프트를 먼저 전환합니다.
        if (TryCycleConnectedHandModeShift(handIndex, character))
        {
            return;
        }

        if (character.HasAlternateForms)
        {
            CycleSameNameHandModeShift(character);
            return;
        }

        StatusText.Text = $"'{character.Name}'에서 전환 가능한 모드시프트를 찾지 못했습니다.";
    }

    private bool TryCycleConnectedHandModeShift(int handIndex, CharacterEntry current)
    {
        string group = NormalizeModeShiftGroup(current.DeckRestrictionGroupId);
        if (group.Length == 0 || !_deckIndexById.TryGetValue(current.Id, out int deckIndex))
        {
            return false;
        }

        CharacterEntry[] members = GetModeShiftLibrary()
            .Where(character => string.Equals(
                NormalizeModeShiftGroup(character.DeckRestrictionGroupId),
                group,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(character => CharacterCategories.GetSortOrder(character.Category))
            .ThenBy(character => character.Name, StringComparer.Ordinal)
            .ThenBy(character => character.Id, StringComparer.Ordinal)
            .ToArray();
        if (members.Length <= 1)
        {
            return false;
        }

        int currentIndex = Array.FindIndex(members, character =>
            string.Equals(character.Id, current.Id, StringComparison.Ordinal));
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        CharacterEntry? target = null;
        for (int offset = 1; offset < members.Length; offset++)
        {
            CharacterEntry candidate = members[(currentIndex + offset) % members.Length];
            bool alreadyUsedElsewhere = _deck
                .Where((_, index) => index != deckIndex)
                .Any(character => string.Equals(character.Id, candidate.Id, StringComparison.Ordinal));
            if (!alreadyUsedElsewhere)
            {
                target = candidate;
                break;
            }
        }

        if (target is null)
        {
            StatusText.Text = "연결형 모드시프트의 다른 형태가 현재 덱의 다른 칸에 이미 편성되어 있습니다.";
            return false;
        }

        try
        {
            string oldId = current.Id;
            var updatedDeck = _deck
                .Select(CharacterLibraryService.Clone)
                .ToList();
            CharacterEntry targetClone = CharacterLibraryService.Clone(target);
            targetClone.ActiveFormId = CharacterEntry.BaseFormId;
            updatedDeck[deckIndex] = targetClone;

            string deckPath = Path.Combine(_dataDirectory, "deck.json");
            DeckDataService.Save(deckPath, updatedDeck);

            _deck = updatedDeck;
            ReplaceSelectedHandCharacter(handIndex, oldId, targetClone);
            RebuildDeckLookups();
            _lastHandSearchSignature = string.Empty;
            _lastDeckSearchSignature = string.Empty;
            BuildCharacterButtons();
            RefreshMainPresetList();
            UpdateMiracleLeaderStatus();
            RenderSelectedHandSlots();

            // 손패 롱프레스로 연결형 모드시프트를 바꾸면 바로 아래 덱 카드도
            // 같은 프레임에서 새 캐릭터/이미지로 보이게 다시 바인딩합니다.
            UpdateCharacterButtons();
            DeckCharacterPanel.InvalidateMeasure();
            DeckCharacterPanel.InvalidateArrange();
            DeckCharacterPanel.InvalidateVisual();
            SaveSettingsSafely();
            ScheduleAutoSearch(handChanged: true);

            StatusText.Text =
                $"현재 손패 {handIndex + 1}번 연결형 모드시프트 · {current.Name} → {targetClone.Name} · 덱 {deckIndex + 1}번과 동기화";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"모드시프트를 덱에 저장하지 못했습니다.\n\n{exception.Message}",
                "모드시프트 저장 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return true;
        }
    }

    private void CycleSameNameHandModeShift(CharacterEntry character)
    {
        FormOptionItem[] options = CreateFormOptions(character);
        if (options.Length <= 1)
        {
            return;
        }

        string currentId = GetSelectedHandFormId(character);
        int currentIndex = Array.FindIndex(options, option =>
            string.Equals(option.Id, currentId, StringComparison.Ordinal));
        int nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % options.Length;
        FormOptionItem next = options[nextIndex];
        SetSelectedHandForm(character.Id, next.Id);
        UpdateCharacterButtons(new[] { character.Id });
        DeckCharacterPanel.InvalidateVisual();
        StatusText.Text = $"현재 손패 동일명 모드시프트 · {character.Name} → {next.DisplayText}";
    }

    private IReadOnlyList<CharacterEntry> GetModeShiftLibrary()
    {
        if (_modeShiftLibraryCache is not null)
        {
            return _modeShiftLibraryCache;
        }

        string libraryPath = Path.Combine(_dataDirectory, "characters.json");
        _modeShiftLibraryCache = CharacterLibraryService.LoadOrCreate(libraryPath, _deck);
        return _modeShiftLibraryCache;
    }

    private static string NormalizeModeShiftGroup(string? value)
        => (value ?? string.Empty).Trim();

    private void ReplaceSelectedHandCharacter(
        int handIndex,
        string oldCharacterId,
        CharacterEntry replacement)
    {
        if (handIndex < 0 || handIndex >= _selectedHandCharacterIds.Count)
        {
            return;
        }

        _selectedHandCharacterIds[handIndex] = replacement.Id;

        string? previousStateId = _selectedHandLetterStateIds.TryGetValue(oldCharacterId, out string? stateId)
            ? stateId
            : null;
        _selectedHandLetterStateIds.Remove(oldCharacterId);
        if (!string.IsNullOrWhiteSpace(previousStateId) &&
            replacement.FindLetterState(previousStateId) is not null)
        {
            _selectedHandLetterStateIds[replacement.Id] = previousStateId;
        }
        else
        {
            _selectedHandLetterStateIds.Remove(replacement.Id);
        }

        _selectedHandFormIds.Remove(oldCharacterId);
        _selectedHandFormIds.Remove(replacement.Id);
        if (replacement.FindForm(replacement.ActiveFormId) is not null)
        {
            _selectedHandFormIds[replacement.Id] = replacement.ActiveFormId!;
        }
    }

    private LetterStateOptionItem[] CreateLetterStateOptions(CharacterEntry character)
        => character.LetterStates
            .Select(state => new LetterStateOptionItem(
                state.Id,
                state.Name,
                CharacterLetterStateKinds.Normalize(state.Kind),
                state.Note,
                isSpecial: true))
            .ToArray();

    private FormOptionItem[] CreateFormOptions(CharacterEntry character)
        => new[]
        {
            new FormOptionItem(
                CharacterEntry.BaseFormId,
                "기본 형태",
                character.ImageFileName,
                character.Letters)
        }
        .Concat((character.AlternateForms ?? new List<CharacterForm>())
            .Select(form => new FormOptionItem(
                form.Id,
                form.Name,
                form.ImageFileName,
                form.Letters)))
        .ToArray();

    private string GetSelectedHandFormId(CharacterEntry character)
    {
        if (_selectedHandFormIds.TryGetValue(character.Id, out string? formId) &&
            character.FindForm(formId) is not null)
        {
            return formId;
        }

        return CharacterEntry.BaseFormId;
    }

    private void SetSelectedHandForm(string characterId, string formId)
    {
        if (!_deckById.TryGetValue(characterId, out CharacterEntry? character))
        {
            return;
        }

        if (string.Equals(formId, CharacterEntry.BaseFormId, StringComparison.Ordinal) ||
            character.FindForm(formId) is null)
        {
            _selectedHandFormIds.Remove(characterId);
        }
        else
        {
            _selectedHandFormIds[characterId] = formId;
        }

        RenderSelectedHandSlots();
        UpdateCharacterButtons(new[] { characterId });
        SaveSettingsSafely();
        ScheduleAutoSearch(handChanged: true);
    }

    private string GetSelectedHandLetterStateId(CharacterEntry character)
    {
        if (_selectedHandLetterStateIds.TryGetValue(character.Id, out string? stateId) &&
            character.FindLetterState(stateId) is not null)
        {
            return stateId;
        }

        return CharacterEntry.BaseLetterStateId;
    }

    private void SetSelectedHandLetterState(string characterId, string stateId)
    {
        if (!_deckById.TryGetValue(characterId, out CharacterEntry? character))
        {
            return;
        }

        if (string.Equals(stateId, CharacterEntry.BaseLetterStateId, StringComparison.Ordinal) ||
            character.FindLetterState(stateId) is null)
        {
            _selectedHandLetterStateIds.Remove(characterId);
        }
        else
        {
            _selectedHandLetterStateIds[characterId] = stateId;
        }

        RenderSelectedHandSlots();
        UpdateCharacterButtons(new[] { characterId });
        SaveSettingsSafely();
        ScheduleAutoSearch(handChanged: true);
    }

    private void ToggleHandCharacter(string characterId)
    {
        string[] previousSelection = _selectedHandCharacterIds.ToArray();
        int existingIndex = _selectedHandCharacterIds.FindIndex(
            id => string.Equals(id, characterId, StringComparison.Ordinal));

        if (existingIndex >= 0)
        {
            _selectedHandCharacterIds.RemoveAt(existingIndex);
            _selectedHandLetterStateIds.Remove(characterId);
            _selectedHandFormIds.Remove(characterId);
        }
        else
        {
            if (_selectedHandCharacterIds.Count >= HandSize)
            {
                StatusText.Text = "현재 손패는 최대 4명입니다. 선택된 캐릭터를 먼저 해제하세요.";
                return;
            }

            _selectedHandCharacterIds.Add(characterId);
        }

        UpdateCharacterButtons(previousSelection
            .Concat(_selectedHandCharacterIds)
            .Append(characterId));
        RenderSelectedHandSlots();
        SaveSettingsSafely();
        ScheduleAutoSearch(handChanged: true);
    }

    private void RemoveHandCharacterAt(int index)
    {
        if (index < 0 || index >= _selectedHandCharacterIds.Count)
        {
            return;
        }

        string[] previousSelection = _selectedHandCharacterIds.ToArray();
        string removedCharacterId = _selectedHandCharacterIds[index];
        _selectedHandCharacterIds.RemoveAt(index);
        _selectedHandLetterStateIds.Remove(removedCharacterId);
        _selectedHandFormIds.Remove(removedCharacterId);
        UpdateCharacterButtons(previousSelection
            .Concat(_selectedHandCharacterIds)
            .Append(removedCharacterId));
        RenderSelectedHandSlots();
        SaveSettingsSafely();
        ScheduleAutoSearch(handChanged: true);
    }

    private void SetActiveBoardCell(string? value, bool advance)
    {
        string? normalizedValue = string.IsNullOrWhiteSpace(value)
            ? null
            : KanaUtility.NormalizeCell(value);

        if (string.Equals(
                _boardCells[_activeBoardIndex],
                normalizedValue,
                StringComparison.Ordinal))
        {
            if (advance && _activeBoardIndex < BoardSize - 1)
            {
                _activeBoardIndex++;
                UpdateBoardButtons();
            }
            return;
        }

        PushBoardHistory();
        _boardCells[_activeBoardIndex] = normalizedValue;

        if (advance && _activeBoardIndex < BoardSize - 1)
        {
            _activeBoardIndex++;
        }

        UpdateBoardButtons();
        SaveSettingsSafely();
        ScheduleAutoSearch();
    }

    private void MoveActiveSlot(int offset)
    {
        _activeBoardIndex = Math.Clamp(_activeBoardIndex + offset, 0, BoardSize - 1);
        UpdateBoardButtons();
    }

    private void PushBoardHistory()
    {
        _boardHistory.Push((string?[])_boardCells.Clone());

        if (_boardHistory.Count <= MaximumBoardHistory)
        {
            return;
        }

        // Stack은 아래쪽 항목을 직접 제거할 수 없으므로 최근 기록만 다시 구성합니다.
        string?[][] recent = _boardHistory
            .Take(MaximumBoardHistory)
            .Reverse()
            .ToArray();
        _boardHistory.Clear();
        foreach (string?[] snapshot in recent)
        {
            _boardHistory.Push(snapshot);
        }
    }

    private void ScheduleAutoSearch(bool handChanged = false)
    {
        if (_isInitializing || AutoSearchCheckBox.IsChecked != true)
        {
            return;
        }

        // 손패를 빠르게 2~4명 연속 선택할 때 매 클릭마다 무거운 검색이 끼어들지 않도록
        // 손패 변경은 조금 더 길게 묶고, 판면 입력은 기존처럼 빠르게 반응시킵니다.
        _autoSearchTimer.Interval = handChanged
            ? TimeSpan.FromMilliseconds(320)
            : TimeSpan.FromMilliseconds(180);
        _autoSearchTimer.Stop();
        _autoSearchTimer.Start();
    }

    private void AutoSearchTimer_Tick(object? sender, EventArgs e)
    {
        _autoSearchTimer.Stop();
        PerformSearch(isAutomatic: true);
    }

    private void PerformSearch(bool isAutomatic)
    {
        IReadOnlyList<CharacterEntry> currentHand = GetSelectedHand();
        ApplyActiveMiracleLeaderEffect(currentHand);
        ApplyActiveDeckGroupLetterEffects(currentHand);

        bool hasBoardLetter = _boardCells.Any(cell => !string.IsNullOrWhiteSpace(cell));

        string generalSearchSignature = BuildGeneralSearchSignature();
        if (!string.Equals(
                _lastGeneralSearchSignature,
                generalSearchSignature,
                StringComparison.Ordinal))
        {
            _lastGeneralResults = hasBoardLetter
                ? _searchService.FindGeneralWordsByLength(_boardCells)
                : CreateEmptySearchGroups();
            _lastGeneralSearchSignature = generalSearchSignature;
            _selectedGeneralWordLength = SelectPreferredLength(
                _lastGeneralResults,
                _selectedGeneralWordLength);
        }

        _lastHandResults = currentHand.Count == 0
            ? CreateEmptySearchGroups()
            : _searchService.FindWordsByLength(_boardCells, currentHand);

        string deckSearchSignature = BuildDeckSearchSignature();
        if (!string.Equals(
                _lastDeckSearchSignature,
                deckSearchSignature,
                StringComparison.Ordinal))
        {
            IReadOnlyList<CharacterEntry> searchDeck = GetSearchDeck();
            ApplyActiveMiracleLeaderEffect(searchDeck);
            ApplyActiveDeckGroupLetterEffects(searchDeck);
            _lastDeckResults = _searchService.FindWordsByLength(_boardCells, searchDeck);
            CalculateFirstTurnProbabilities(_lastDeckResults, searchDeck);
            _lastDeckSearchSignature = deckSearchSignature;
        }

        string handSearchSignature = BuildHandSearchSignature(currentHand);
        bool handSelectionChanged = !string.Equals(
            _lastHandSearchSignature,
            handSearchSignature,
            StringComparison.Ordinal);
        _lastHandSearchSignature = handSearchSignature;

        _selectedHandWordLength = handSelectionChanged
            ? SelectLongestAvailableLength(_lastHandResults)
            : SelectPreferredLength(_lastHandResults, _selectedHandWordLength);
        _selectedDeckWordLength = SelectPreferredLength(_lastDeckResults, _selectedDeckWordLength);
        _lastHandEmptyMessage = currentHand.Count == 0
            ? "손패 캐릭터를 선택하면 현재 손패 결과가 표시됩니다."
            : "현재 손패로 만들 수 있는 4~7글자 단어가 없습니다.";
        _lastDeckEmptyMessage = "덱 전체에서도 만들 수 있는 4~7글자 단어가 없습니다.";
        HandResultsTitleText.Text = "현재 손패로 가능";
        _hasPerformedSearch = true;

        RenderStoredSearchResults();
        SaveSettingsSafely();

        string prefix = isAutomatic ? "자동 검색" : "검색 완료";
        string handLabel = currentHand.Count == 0
            ? "손패 미선택"
            : $"손패 {FormatResultSetSummary(_lastHandResults)}";
        StatusText.Text =
            $"{prefix} · 일반 {FormatResultSetSummary(_lastGeneralResults)} · {handLabel} · 덱 전체 {FormatResultSetSummary(_lastDeckResults)}";
    }

    private void RenderStoredSearchResults()
    {
        RenderGeneralSuggestionToggle();

        if (!_hasPerformedSearch)
        {
            RenderInitialResultPanels();
            return;
        }

        if (_selectedHandCharacterIds.Count == 0)
        {
            RenderHandWaitingMessage();
        }
        else
        {
            RenderSearchResultsByLength(
                HandResultsPanel,
                _lastHandResults,
                _lastHandEmptyMessage,
                isHandResults: true,
                isGeneralSearch: false,
                isDeckResults: false);
        }

        RenderSearchResultsByLength(
            DeckResultsPanel,
            _lastDeckResults,
            _lastDeckEmptyMessage,
            isHandResults: false,
            isGeneralSearch: false,
            isDeckResults: true);
    }

    private void RenderGeneralSuggestionToggle()
    {
        bool hasBoardLetter = _boardCells.Any(cell => !string.IsNullOrWhiteSpace(cell));
        UpdateGeneralLengthToggleStates();

        if (!hasBoardLetter)
        {
            GeneralSuggestionTitleText.Text = "일반 문자 검색 · 판면을 입력하면 손패와 관계없이 자동 검색";
            ShowGeneralSuggestionMessage(
                "판면 문자를 하나 이상 입력하면 7~4글자 일반 후보가 표로 표시됩니다.",
                "#9FB2C2");
            return;
        }

        int totalCount = Enumerable.Range(4, 4)
            .Select(length => GetSearchGroup(_lastGeneralResults, length).Results.Count)
            .Sum();
        GeneralSuggestionTitleText.Text = totalCount > 0
            ? $"일반 문자 검색 · 총 {totalCount:N0}개 · 손패와 무관"
            : "일반 문자 검색 · 후보 없음";

        SearchGroup group = GetSearchGroup(_lastGeneralResults, _selectedGeneralWordLength);
        if (!group.HasResults)
        {
            int availableLength = SelectLongestAvailableLength(_lastGeneralResults);
            SearchGroup available = GetSearchGroup(_lastGeneralResults, availableLength);
            if (available.HasResults)
            {
                _selectedGeneralWordLength = availableLength;
                UpdateGeneralLengthToggleStates();
                group = available;
            }
        }

        if (!group.HasResults)
        {
            ShowGeneralSuggestionMessage(
                "현재 판면에 이어지는 4~7글자 일반 후보가 없습니다.",
                "#D8B978");
            return;
        }

        SearchResult[] displayedResults = group.Results
            .OrderByDescending(result => result.ComboCount)
            .ThenBy(result => result.Assignments.Count)
            .ThenBy(result => result.StartIndex)
            .ThenBy(result => result.Word, StringComparer.Ordinal)
            .GroupBy(result => result.Word, StringComparer.Ordinal)
            .Select(grouping => grouping.First())
            .Take(120)
            .ToArray();

        GeneralSuggestionTableRow[] rows = displayedResults
            .Select((result, index) =>
            {
                CharacterAssignment[] assignments = result.Assignments
                    .Where(assignment => assignment.IsGeneralSuggestion)
                    .OrderBy(assignment => assignment.BoardIndex)
                    .ToArray();

                string requiredLetters = assignments.Length == 0
                    ? "—"
                    : string.Join(" · ", assignments.Select(assignment => assignment.Letter));
                string placement = assignments.Length == 0
                    ? "—"
                    : string.Join(", ", assignments.Select(assignment =>
                        $"{assignment.BoardIndex + 1}:{assignment.Letter}"));

                return new GeneralSuggestionTableRow
                {
                    Number = index + 1,
                    Word = result.Word,
                    RequiredLetters = requiredLetters,
                    Placement = placement,
                    ComboCount = result.ComboCount
                };
            })
            .ToArray();

        GeneralSuggestionWordsGrid.ItemsSource = rows;
        GeneralSuggestionWordsGrid.Visibility = Visibility.Visible;
        GeneralSuggestionEmptyText.Visibility = Visibility.Collapsed;

        int hiddenCount = Math.Max(0, group.Results.Count - displayedResults.Length);
        GeneralSuggestionTitleText.Text = hiddenCount > 0
            ? $"일반 문자 검색 · {group.WordLength}글자 {group.Results.Count:N0}개 · 상위 {displayedResults.Length:N0}개 표시"
            : $"일반 문자 검색 · {group.WordLength}글자 {group.Results.Count:N0}개";
    }

    private void ShowGeneralSuggestionMessage(string message, string color)
    {
        GeneralSuggestionWordsGrid.ItemsSource = null;
        GeneralSuggestionWordsGrid.Visibility = Visibility.Collapsed;
        GeneralSuggestionEmptyText.Text = message;
        GeneralSuggestionEmptyText.Foreground = BrushFromHex(color);
        GeneralSuggestionEmptyText.Visibility = Visibility.Visible;
    }

    private void GeneralSuggestionLengthToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle ||
            !int.TryParse(toggle.Tag?.ToString(), out int length) ||
            length is < 4 or > 7)
        {
            return;
        }

        _selectedGeneralWordLength = length;
        UpdateGeneralLengthToggleStates();
        RenderGeneralSuggestionToggle();
    }

    private void UpdateGeneralLengthToggleStates()
    {
        GeneralLength7Toggle.IsChecked = _selectedGeneralWordLength == 7;
        GeneralLength6Toggle.IsChecked = _selectedGeneralWordLength == 6;
        GeneralLength5Toggle.IsChecked = _selectedGeneralWordLength == 5;
        GeneralLength4Toggle.IsChecked = _selectedGeneralWordLength == 4;

        GeneralLength7Toggle.Content = FormatGeneralLengthToggleText(7);
        GeneralLength6Toggle.Content = FormatGeneralLengthToggleText(6);
        GeneralLength5Toggle.Content = FormatGeneralLengthToggleText(5);
        GeneralLength4Toggle.Content = FormatGeneralLengthToggleText(4);
    }

    private string FormatGeneralLengthToggleText(int length)
    {
        int count = GetSearchGroup(_lastGeneralResults, length).Results.Count;
        return count > 0 ? $"{length}글자 ({count:N0})" : $"{length}글자";
    }

    private void RenderHandWaitingMessage()
    {
        HandResultsPanel.Children.Clear();
        HandResultsPanel.Children.Add(new TextBlock
        {
            Text = "손패 캐릭터를 선택하면 이 영역에 현재 손패로 만들 수 있는 후보가 표시됩니다.",
            Foreground = BrushFromHex("#AEB8C8"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 4)
        });
    }

    private IReadOnlyList<CharacterEntry> GetSelectedHand()
    {
        return _selectedHandCharacterIds
            .Select(id => _deckById.TryGetValue(id, out CharacterEntry? character) ? character : null)
            .Where(character => character is not null)
            .Cast<CharacterEntry>()
            .Select(character =>
            {
                CharacterEntry clone = CharacterLibraryService.Clone(character);
                clone.ActiveLetterStateId = GetSelectedHandLetterStateId(character);
                clone.ActiveFormId = GetSelectedHandFormId(character);
                clone.ActiveMiracleGrantedLetters.Clear();
                clone.ActiveMiracleLeaderName = string.Empty;
                clone.ActiveMiracleEffectNote = string.Empty;
                clone.ActiveDeckGroupGrantedLetters.Clear();
                clone.ActiveDeckGroupConditionText = string.Empty;
                clone.ActiveDeckGroupEffectNote = string.Empty;
                return clone;
            })
            .ToArray();
    }

    private IReadOnlyList<CharacterEntry> GetSearchDeck()
        => _deck
            .Select(character =>
            {
                CharacterEntry clone = CharacterLibraryService.Clone(character);
                clone.ActiveLetterStateId = CharacterEntry.BaseLetterStateId;
                clone.ActiveFormId = CharacterEntry.AllFormsId;
                clone.ActiveMiracleGrantedLetters.Clear();
                clone.ActiveMiracleLeaderName = string.Empty;
                clone.ActiveMiracleEffectNote = string.Empty;
                clone.ActiveDeckGroupGrantedLetters.Clear();
                clone.ActiveDeckGroupConditionText = string.Empty;
                clone.ActiveDeckGroupEffectNote = string.Empty;
                return clone;
            })
            .ToArray();

    private void CalculateFirstTurnProbabilities(
        IReadOnlyDictionary<int, SearchGroup> groups,
        IReadOnlyList<CharacterEntry> searchDeck)
    {
        SearchResult[] results = groups.Values
            .SelectMany(group => group.Results)
            .ToArray();

        if (results.Length == 0 || searchDeck.Count == 0)
        {
            return;
        }

        IReadOnlyList<IReadOnlyList<CharacterEntry>> firstTurnHands =
            BuildFirstTurnVisibleHands(searchDeck);
        int total = firstTurnHands.Count;
        var cache = new Dictionary<string, (int Success, int Total)>(StringComparer.Ordinal);

        foreach (SearchResult result in results)
        {
            string key = string.Join(
                "\u001F",
                result.Assignments
                    .Select(assignment => KanaUtility.NormalizeCell(assignment.Letter))
                    .OrderBy(letter => letter, StringComparer.Ordinal));

            if (!cache.TryGetValue(key, out var probability))
            {
                int success = firstTurnHands.Count(hand =>
                    _searchService.CanAssignResult(result, hand));
                probability = (success, total);
                cache[key] = probability;
            }

            result.FirstTurnSuccessCount = probability.Success;
            result.FirstTurnCombinationCount = probability.Total;
        }
    }

    private static IReadOnlyList<IReadOnlyList<CharacterEntry>> BuildFirstTurnVisibleHands(
        IReadOnlyList<CharacterEntry> deck)
    {
        if (deck.Count == 0)
        {
            return Array.Empty<IReadOnlyList<CharacterEntry>>();
        }

        CharacterEntry leader = deck[0];
        CharacterEntry[] remaining = deck.Skip(1).ToArray();
        int drawCount = Math.Min(6, remaining.Length);

        if (drawCount == 0)
        {
            return new[] { (IReadOnlyList<CharacterEntry>)new[] { leader } };
        }

        var hands = new List<IReadOnlyList<CharacterEntry>>();
        var selected = new CharacterEntry[drawCount];

        void Build(int startIndex, int selectedCount)
        {
            if (selectedCount == drawCount)
            {
                var hand = new CharacterEntry[drawCount + 1];
                hand[0] = leader;
                Array.Copy(selected, 0, hand, 1, drawCount);
                hands.Add(hand);
                return;
            }

            int remainingNeeded = drawCount - selectedCount;
            for (int index = startIndex;
                 index <= remaining.Length - remainingNeeded;
                 index++)
            {
                selected[selectedCount] = remaining[index];
                Build(index + 1, selectedCount + 1);
            }
        }

        Build(0, 0);
        return hands;
    }

    private void ApplyActiveMiracleLeaderEffect(IReadOnlyList<CharacterEntry> characters)
    {
        CharacterEntry? leader = _deck.FirstOrDefault();
        MiracleLeaderEffect effect = DeckDataService.NormalizeMiracleLeaderEffect(
            leader?.MiracleLeaderEffect);
        if (leader is null || !effect.IsConfigured)
        {
            return;
        }

        foreach (CharacterEntry character in characters)
        {
            if (!DeckDataService.CharacterMatchesTargetGroups(character, effect.TargetGroups))
            {
                continue;
            }

            character.ActiveMiracleGrantedLetters = effect.GrantedLetters.ToList();
            character.ActiveMiracleLeaderName = leader.Name;
            character.ActiveMiracleEffectNote = effect.Note;
        }
    }

    private void ApplyActiveDeckGroupLetterEffects(IReadOnlyList<CharacterEntry> characters)
    {
        foreach (CharacterEntry character in characters)
        {
            character.ActiveDeckGroupGrantedLetters.Clear();
            character.ActiveDeckGroupConditionText = string.Empty;
            character.ActiveDeckGroupEffectNote = string.Empty;

            DeckGroupLetterEffect effect = DeckDataService.NormalizeDeckGroupLetterEffect(
                character.DeckGroupLetterEffect);
            if (!effect.IsConfigured)
            {
                continue;
            }

            int matchingCount = _deck.Count(deckCharacter =>
                DeckDataService.CharacterMatchesTargetGroups(deckCharacter, effect.TargetGroups));
            if (matchingCount < effect.MinimumCount)
            {
                continue;
            }

            character.ActiveDeckGroupGrantedLetters = effect.GrantedLetters.ToList();
            string groupText = string.Join(" · ", effect.TargetGroups);
            character.ActiveDeckGroupConditionText =
                $"{groupText} 덱 {matchingCount}명 / 필요 {effect.MinimumCount}명";
            character.ActiveDeckGroupEffectNote = effect.Note;
        }
    }

    private CharacterEntry CreateDisplayCharacter(
        CharacterEntry character,
        string? stateId,
        string? formId = null)
    {
        CharacterEntry clone = CharacterLibraryService.Clone(character);
        clone.ActiveLetterStateId = stateId;
        clone.ActiveFormId = formId ?? CharacterEntry.BaseFormId;
        clone.ActiveMiracleGrantedLetters.Clear();
        clone.ActiveMiracleLeaderName = string.Empty;
        clone.ActiveMiracleEffectNote = string.Empty;
        clone.ActiveDeckGroupGrantedLetters.Clear();
        clone.ActiveDeckGroupConditionText = string.Empty;
        clone.ActiveDeckGroupEffectNote = string.Empty;
        ApplyActiveMiracleLeaderEffect(new[] { clone });
        ApplyActiveDeckGroupLetterEffects(new[] { clone });
        return clone;
    }

    private void UpdateMiracleLeaderStatus()
    {
        CharacterEntry? leader = _deck.FirstOrDefault();
        if (leader is null)
        {
            MiracleLeaderStatusText.Text = "현재 덱에 리더가 없습니다.";
            MiracleLeaderStatusText.Foreground = BrushFromHex("#E0C978");
            MiracleLeaderStatusText.ToolTip = null;
            return;
        }

        MiracleLeaderEffect effect = DeckDataService.NormalizeMiracleLeaderEffect(
            leader.MiracleLeaderEffect);
        if (!effect.IsConfigured)
        {
            MiracleLeaderStatusText.Text = $"리더: {leader.Name} · 미라클 문자 부여 효과 없음";
            MiracleLeaderStatusText.Foreground = BrushFromHex("#8FA0B4");
            MiracleLeaderStatusText.ToolTip = "덱 1번 캐릭터가 리더입니다.";
            return;
        }

        CharacterEntry[] matchingCharacters = _deck
            .Where(character => DeckDataService.CharacterMatchesTargetGroups(
                character, effect.TargetGroups))
            .ToArray();

        if (matchingCharacters.Length == 0)
        {
            MiracleLeaderStatusText.Text =
                $"리더: {leader.Name} · 미라클 효과 설정됨 · 현재 덱에서 대상 그룹 캐릭터 0명";
            MiracleLeaderStatusText.Foreground = BrushFromHex("#FFD08A");
            MiracleLeaderStatusText.ToolTip =
                $"대상 그룹: {string.Join(" · ", effect.TargetGroups)}\n" +
                $"부여 문자: {string.Join(" · ", effect.GrantedLetters)}\n" +
                "현재 덱 캐릭터의 소속 그룹 또는 포괄 그룹 규칙과 일치하는 대상이 없습니다.";
            return;
        }

        MiracleLeaderStatusText.Text =
            $"리더: {leader.Name} · 미라클 문자 부여 활성 · 적용 대상 {matchingCharacters.Length}명 · +{string.Join(" · ", effect.GrantedLetters)}";
        MiracleLeaderStatusText.Foreground = BrushFromHex("#D9C2FF");
        string targetNames = string.Join(" · ", matchingCharacters.Select(character => character.Name));
        MiracleLeaderStatusText.ToolTip =
            $"대상 그룹: {string.Join(" · ", effect.TargetGroups)}\n" +
            $"적용 캐릭터: {targetNames}\n" +
            (string.IsNullOrWhiteSpace(effect.Note)
                ? "덱 1번 캐릭터가 리더일 때만 적용됩니다."
                : $"덱 1번 캐릭터가 리더일 때만 적용됩니다.\n{effect.Note}");
    }

    private static string BuildCharacterToolTip(CharacterEntry character, bool isLeader)
    {
        var lines = new List<string>
        {
            $"{(isLeader ? "[리더] " : string.Empty)}{character.Name}",
            $"현재 자체 문자: {string.Join(" · ", character.GetOwnAvailableLetters())}"
        };
        if (!string.IsNullOrWhiteSpace(character.GroupName))
        {
            lines.Add($"소속 그룹: {character.GroupName}");
            List<string> includedGroups = DeckDataService.GetEffectiveGroupNames(character)
                .Where(group => !string.Equals(
                    group,
                    DeckDataService.NormalizeGroupName(character.GroupName),
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (includedGroups.Count > 0)
            {
                lines.Add($"같이 취급되는 그룹: {string.Join(" · ", includedGroups)}");
            }
        }
        if (character.HasAlternateForms)
        {
            lines.Add($"동일 이름 모드시프트: 기본 + {character.AlternateForms.Count}개");
            if (!string.Equals(character.ActiveFormId, CharacterEntry.BaseFormId, StringComparison.Ordinal) &&
                !string.Equals(character.ActiveFormId, CharacterEntry.AllFormsId, StringComparison.Ordinal))
            {
                lines.Add($"현재 형태: {character.GetActiveFormName()}");
            }
        }
        if (character.LetterStates.Count > 0)
        {
            lines.Add($"문자 상태 {character.LetterStates.Count}개");
        }
        if (character.HasActiveMiracleGrant)
        {
            lines.Add($"현재 미라클 부여: {string.Join(" · ", character.ActiveMiracleGrantedLetters)} ({character.ActiveMiracleLeaderName} 리더)");
        }
        if (character.HasActiveDeckGroupGrant)
        {
            lines.Add($"현재 덱 조건 문자: {string.Join(" · ", character.ActiveDeckGroupGrantedLetters)} · {character.ActiveDeckGroupConditionText}");
        }
        DeckGroupLetterEffect deckGroupEffect = DeckDataService.NormalizeDeckGroupLetterEffect(
            character.DeckGroupLetterEffect);
        if (deckGroupEffect.IsConfigured)
        {
            lines.Add($"덱 조건: {string.Join(" · ", deckGroupEffect.TargetGroups)} {deckGroupEffect.MinimumCount}명 이상 → +{string.Join(" · ", deckGroupEffect.GrantedLetters)}");
        }
        MiracleLeaderEffect effect = DeckDataService.NormalizeMiracleLeaderEffect(
            character.MiracleLeaderEffect);
        if (isLeader && effect.IsConfigured)
        {
            lines.Add($"미라클 리더 효과: {string.Join(" · ", effect.TargetGroups)} 그룹에 {string.Join(" · ", effect.GrantedLetters)} 부여");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private void RenderSearchResultsByLength(
        Panel panel,
        IReadOnlyDictionary<int, SearchGroup> groups,
        string emptyMessage,
        bool isHandResults,
        bool isGeneralSearch,
        bool isDeckResults)
    {
        panel.Children.Clear();

        bool hasAnyResults = groups.Values.Any(group => group.HasResults);
        if (!hasAnyResults)
        {
            panel.Children.Add(new TextBlock
            {
                Text = emptyMessage,
                Foreground = BrushFromHex("#AEB8C8"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 2, 4)
            });
            return;
        }

        if (isGeneralSearch)
        {
            panel.Children.Add(new Border
            {
                Background = BrushFromHex("#1E3040"),
                BorderBrush = BrushFromHex("#3B7892"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 12),
                Child = new TextBlock
                {
                    Text = "손패를 선택하지 않아 현재 판면과 이어지는 일반 단어를 검색합니다. 아래 문자는 단어를 만들기 위해 필요한 글자입니다.",
                    Foreground = BrushFromHex("#B8EAF5"),
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        int selectedLength = isHandResults
            ? _selectedHandWordLength
            : _selectedDeckWordLength;

        var lengthTabs = new UniformGrid
        {
            Columns = 4,
            Margin = new Thickness(0, 0, 0, 12)
        };

        foreach (int length in Enumerable.Range(4, 4).Reverse())
        {
            SearchGroup group = GetSearchGroup(groups, length);
            bool isSelected = length == selectedLength;
            var button = new Button
            {
                Content = $"{length}글자  {group.Results.Count}개",
                Tag = length,
                Style = (Style)FindResource(
                    isSelected ? "PrimaryButtonStyle" : "CompactButtonStyle"),
                Margin = new Thickness(3, 0, 3, 0),
                Padding = new Thickness(8, 8, 8, 8),
                ToolTip = group.HasResults
                    ? $"{length}글자 후보 {group.Results.Count}개 보기"
                    : $"현재 조건에서 {length}글자 후보 없음"
            };

            button.Click += (_, _) =>
            {
                int newLength = (int)button.Tag;
                if (isHandResults)
                {
                    _selectedHandWordLength = newLength;
                }
                else
                {
                    _selectedDeckWordLength = newLength;
                }

                RenderStoredSearchResults();
                string resultLabel = isGeneralSearch
                    ? "판면 일반 추천"
                    : isHandResults ? "현재 손패" : "덱 전체";
                StatusText.Text = $"{resultLabel} · {newLength}글자 결과를 표시합니다.";
            };
            lengthTabs.Children.Add(button);
        }

        panel.Children.Add(lengthTabs);
        SearchGroup selectedGroup = GetSearchGroup(groups, selectedLength);
        RenderSearchGroupContent(panel, selectedGroup, isGeneralSearch, isDeckResults);
    }

    private void RenderSearchGroupContent(
        Panel panel,
        SearchGroup group,
        bool isGeneralSearch,
        bool isDeckResults)
    {
        if (!group.HasResults)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"현재 조건에서 만들 수 있는 {group.WordLength}글자 단어가 없습니다. 다른 글자 수를 선택해 보세요.",
                Foreground = BrushFromHex("#E0C978"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 2, 4)
            });
            return;
        }

        IOrderedEnumerable<SearchResult> orderedResults;
        if (isDeckResults)
        {
            orderedResults = _deckResultSortMode switch
            {
                "Probability" => group.Results
                    .OrderByDescending(result => result.FirstTurnSuccessRate)
                    .ThenByDescending(result => result.ComboCount)
                    .ThenBy(result => result.Assignments.Count),
                "Combo" => group.Results
                    .OrderByDescending(result => result.ComboCount)
                    .ThenByDescending(result => result.FirstTurnSuccessRate)
                    .ThenBy(result => result.Assignments.Count),
                _ => group.Results
                    .OrderByDescending(result => result.PracticalScore)
                    .ThenByDescending(result => result.ComboCount)
                    .ThenByDescending(result => result.FirstTurnSuccessRate)
                    .ThenBy(result => result.Assignments.Count)
            };
        }
        else
        {
            orderedResults = group.Results
                .OrderByDescending(result => result.ComboCount)
                .ThenBy(result => result.Assignments.Count);
        }

        SearchResult[] displayedResults = orderedResults
            .ThenBy(result => result.StartIndex)
            .ThenBy(result => result.Word, StringComparer.Ordinal)
            .Take(30)
            .ToArray();

        bool hasCompleteComboData = _hasCompleteComboData;

        if (isGeneralSearch)
        {
            string letterSummary = string.Join(
                "  ·  ",
                displayedResults
                    .SelectMany(result => result.Assignments)
                    .Where(assignment => assignment.IsGeneralSuggestion)
                    .GroupBy(assignment => assignment.Letter, StringComparer.Ordinal)
                    .OrderByDescending(grouping => grouping.Count())
                    .ThenBy(grouping => grouping.Key, StringComparer.Ordinal)
                    .Take(14)
                    .Select(grouping => $"{grouping.Key} ({grouping.Count()})"));

            panel.Children.Add(new Border
            {
                Background = BrushFromHex("#263746"),
                BorderBrush = BrushFromHex("#547086"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(letterSummary)
                        ? "추천 문자를 계산할 수 없습니다."
                        : $"자주 필요한 문자 · {letterSummary}",
                    Foreground = BrushFromHex("#D7F4FA"),
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = isDeckResults
                ? $"{group.WordLength}글자 후보 {group.Results.Count}개 · {GetDeckSortDescription()} · 리더 고정 + 나머지 6명"
                : hasCompleteComboData
                    ? $"{group.WordLength}글자 후보 {group.Results.Count}개 · 콤보 높은 순"
                    : $"{group.WordLength}글자 후보 {group.Results.Count}개 · 임시 콤보 높은 순",
            Foreground = BrushFromHex("#66D9EF"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 2, 10),
            TextWrapping = TextWrapping.Wrap
        });

        SearchResult[] featured = displayedResults.Take(2).ToArray();
        foreach (SearchResult result in featured)
        {
            panel.Children.Add(CreateResultCard(
                result,
                featured: true,
                hasCompleteComboData: hasCompleteComboData,
                showFirstTurnProbability: isDeckResults));
        }

        SearchResult[] remaining = displayedResults.Skip(2).ToArray();
        if (remaining.Length == 0)
        {
            return;
        }

        var remainingPanel = new StackPanel();
        foreach (SearchResult result in remaining)
        {
            remainingPanel.Children.Add(CreateResultCard(
                result,
                featured: false,
                hasCompleteComboData: hasCompleteComboData,
                showFirstTurnProbability: isDeckResults));
        }

        panel.Children.Add(new Expander
        {
            Header = $"나머지 {remaining.Length}개 결과 보기",
            Foreground = BrushFromHex("#D7DEEA"),
            Margin = new Thickness(2, 4, 2, 0),
            IsExpanded = false,
            Content = remainingPanel
        });
    }

    private static SearchGroup GetSearchGroup(
        IReadOnlyDictionary<int, SearchGroup> groups,
        int length)
    {
        return groups.TryGetValue(length, out SearchGroup? group)
            ? group
            : new SearchGroup { WordLength = length };
    }

    private static int SelectPreferredLength(
        IReadOnlyDictionary<int, SearchGroup> groups,
        int currentLength)
    {
        if (groups.TryGetValue(currentLength, out SearchGroup? currentGroup) && currentGroup.HasResults)
        {
            return currentLength;
        }

        return SelectLongestAvailableLength(groups);
    }

    private static int SelectLongestAvailableLength(
        IReadOnlyDictionary<int, SearchGroup> groups)
    {
        for (int length = 7; length >= 4; length--)
        {
            if (groups.TryGetValue(length, out SearchGroup? group) && group.HasResults)
            {
                return length;
            }
        }

        return 7;
    }

    private string BuildGeneralSearchSignature()
        => string.Join("\u001E", _boardCells.Select(cell => cell ?? "□"));

    private string BuildDeckSearchSignature()
    {
        string boardSignature = string.Join(
            "\u001E",
            _boardCells.Select(cell => cell ?? "□"));
        string deckSignature = string.Join(
            "\u001F",
            _deck.Select(character => character.Id));
        return $"{boardSignature}\u001D{deckSignature}";
    }

    private static string BuildHandSearchSignature(
        IReadOnlyList<CharacterEntry> currentHand)
    {
        if (currentHand.Count == 0)
        {
            return "NO_HAND";
        }

        return string.Join(
            "||",
            currentHand.Select(character =>
            {
                string letters = string.Join(
                    ",",
                    character.GetAvailableLetters(character.ActiveLetterStateId)
                        .OrderBy(letter => letter, StringComparer.Ordinal));
                string miracleLetters = string.Join(
                    ",",
                    character.ActiveMiracleGrantedLetters
                        .OrderBy(letter => letter, StringComparer.Ordinal));
                string deckGroupLetters = string.Join(
                    ",",
                    character.ActiveDeckGroupGrantedLetters
                        .OrderBy(letter => letter, StringComparer.Ordinal));

                return string.Join(
                    "|",
                    character.Id,
                    character.ActiveFormId,
                    character.ActiveLetterStateId,
                    letters,
                    miracleLetters,
                    deckGroupLetters);
            }));
    }

    private static IReadOnlyDictionary<int, SearchGroup> CreateEmptySearchGroups()
    {
        return Enumerable.Range(4, 4)
            .ToDictionary(
                length => length,
                length => new SearchGroup { WordLength = length });
    }

    private static string FormatResultSetSummary(
        IReadOnlyDictionary<int, SearchGroup> groups)
    {
        SearchGroup? longest = Enumerable.Range(4, 4)
            .Reverse()
            .Select(length => GetSearchGroup(groups, length))
            .FirstOrDefault(group => group.HasResults);

        if (longest is null)
        {
            return "결과 없음";
        }

        int total = groups.Values.Sum(group => group.Results.Count);
        return $"최장 {longest.WordLength}글자 {longest.Results.Count}개 · 전체 {total}개";
    }

    private Border CreateResultCard(
        SearchResult result,
        bool featured,
        bool hasCompleteComboData,
        bool showFirstTurnProbability)
    {
        var card = new Border
        {
            Background = featured
                ? BrushFromHex("#203342")
                : BrushFromHex("#202735"),
            BorderBrush = featured
                ? BrushFromHex("#3B7892")
                : BrushFromHex("#303C4F"),
            BorderThickness = new Thickness(featured ? 2 : 1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(featured ? 14 : 11),
            Margin = new Thickness(0, 0, 0, 9)
        };

        var stack = new StackPanel();
        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        titleGrid.Children.Add(new TextBlock
        {
            Text = result.Word,
            FontSize = featured ? 25 : 19,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var badgePanel = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        badgePanel.Children.Add(CreateResultBadge(
            $"{result.Cells.Count}글자",
            "#2B4D66",
            "#5EA7D1",
            featured));
        badgePanel.Children.Add(CreateResultBadge(
            hasCompleteComboData
                ? $"예상 {result.ComboCount}콤보"
                : $"임시 {result.ComboCount}콤보",
            hasCompleteComboData ? "#2C6B55" : "#6B5A2C",
            hasCompleteComboData ? "#63C99A" : "#D5B85C",
            featured));

        if (showFirstTurnProbability && result.FirstTurnCombinationCount > 0)
        {
            (string background, string border) = GetProbabilityColors(result.FirstTurnSuccessRate);
            badgePanel.Children.Add(CreateResultBadge(
                $"첫 턴 {result.FirstTurnSuccessRate:P1}",
                background,
                border,
                featured,
                $"{result.FirstTurnSuccessCount:N0} / {result.FirstTurnCombinationCount:N0} 구성에서 성립\n리더는 1번 손패에 고정"));
        }

        Grid.SetColumn(badgePanel, 1);
        titleGrid.Children.Add(badgePanel);
        stack.Children.Add(titleGrid);

        stack.Children.Add(CreateWordCompositionPreview(result, featured));

        stack.Children.Add(new TextBlock
        {
            Text = $"{result.Cells.Count}글자 단어 · 판면 {result.StartIndex + 1}~{result.EndIndex + 1}칸에 배치",
            Foreground = BrushFromHex("#AEB8C8"),
            Margin = new Thickness(2, 3, 0, 5),
            TextWrapping = TextWrapping.Wrap
        });

        stack.Children.Add(new Expander
        {
            Header = "전체 7칸 판면 보기",
            Foreground = BrushFromHex("#B8EAF5"),
            Margin = new Thickness(0, 1, 0, 8),
            IsExpanded = false,
            Content = CreateFullBoardPreview(result, featured)
        });

        int twoLetter = result.ComboMatches.Count(match => match.WordLength == 2);
        int threeLetter = result.ComboMatches.Count(match => match.WordLength == 3);
        int longWords = result.ComboMatches.Count(match => match.WordLength >= 4);
        stack.Children.Add(new TextBlock
        {
            Text = hasCompleteComboData
                ? $"콤보 구성 · 2글자 {twoLetter} · 3글자 {threeLetter} · 4글자 이상 {longWords}"
                : $"현재 계산 · 4글자 이상 {longWords}  (2~3글자 데이터 수집 후 정확한 콤보 표시)",
            Foreground = hasCompleteComboData
                ? BrushFromHex("#9FE1BF")
                : BrushFromHex("#E0C978"),
            FontSize = featured ? 12 : 11,
            Margin = new Thickness(0, 0, 0, 7),
            TextWrapping = TextWrapping.Wrap
        });

        CharacterAssignment[] alternateFormAssignments = result.Assignments
            .Where(assignment => assignment.UsesAlternateForm)
            .ToArray();
        if (alternateFormAssignments.Length > 0)
        {
            string formText = string.Join(
                Environment.NewLine,
                alternateFormAssignments
                    .GroupBy(assignment => assignment.CharacterId, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        CharacterAssignment assignment = group.First();
                        string letters = string.Join(" · ", group
                            .Select(item => item.Letter)
                            .Distinct(StringComparer.Ordinal));
                        return $"⇄ {assignment.CharacterName}: {assignment.CharacterFormName} 형태 · {letters}";
                    }));

            stack.Children.Add(new Border
            {
                Background = BrushFromHex("#243F35"),
                BorderBrush = BrushFromHex("#5FAE88"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock
                {
                    Text = formText,
                    Foreground = BrushFromHex("#BFF0D3"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = featured ? 12 : 11
                }
            });
        }

        CharacterAssignment[] specialStateAssignments = result.Assignments
            .Where(assignment => assignment.UsesSpecialLetterState)
            .ToArray();
        if (specialStateAssignments.Length > 0)
        {
            string warningText = string.Join(
                Environment.NewLine,
                specialStateAssignments
                    .GroupBy(assignment => assignment.CharacterId, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        CharacterAssignment assignment = group.First();
                        string note = string.IsNullOrWhiteSpace(assignment.LetterStateNote)
                            ? string.Empty
                            : $" · {assignment.LetterStateNote}";
                        return $"⚠ {assignment.CharacterName}: {assignment.LetterStateName} ({assignment.LetterStateKind}){note}";
                    }));

            stack.Children.Add(new Border
            {
                Background = BrushFromHex("#493D24"),
                BorderBrush = BrushFromHex("#C9A44C"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock
                {
                    Text = warningText,
                    Foreground = BrushFromHex("#FFE3A3"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = featured ? 12 : 11
                }
            });
        }

        CharacterAssignment[] miracleAssignments = result.Assignments
            .Where(assignment => assignment.UsesMiracleLeaderLetter)
            .ToArray();
        if (miracleAssignments.Length > 0)
        {
            string warningText = string.Join(
                Environment.NewLine,
                miracleAssignments
                    .GroupBy(assignment => assignment.CharacterId, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        CharacterAssignment assignment = group.First();
                        string letters = string.Join(" · ", group
                            .Select(item => item.Letter)
                            .Distinct(StringComparer.Ordinal));
                        string groupText = string.IsNullOrWhiteSpace(assignment.CharacterGroupName)
                            ? "그룹 미지정"
                            : assignment.CharacterGroupName;
                        string note = string.IsNullOrWhiteSpace(assignment.MiracleEffectNote)
                            ? string.Empty
                            : $" · {assignment.MiracleEffectNote}";
                        return $"✨ {assignment.CharacterName}: {letters} · {assignment.MiracleLeaderName} 리더 효과 ({groupText}){note}";
                    }));

            stack.Children.Add(new Border
            {
                Background = BrushFromHex("#392E52"),
                BorderBrush = BrushFromHex("#9A7BD4"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock
                {
                    Text = warningText,
                    Foreground = BrushFromHex("#E4D4FF"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = featured ? 12 : 11
                }
            });
        }

        CharacterAssignment[] deckGroupAssignments = result.Assignments
            .Where(assignment => assignment.UsesDeckGroupConditionLetter)
            .ToArray();
        if (deckGroupAssignments.Length > 0)
        {
            string warningText = string.Join(
                Environment.NewLine,
                deckGroupAssignments
                    .GroupBy(assignment => assignment.CharacterId, StringComparer.Ordinal)
                    .Select(group =>
                    {
                        CharacterAssignment assignment = group.First();
                        string letters = string.Join(" · ", group
                            .Select(item => item.Letter)
                            .Distinct(StringComparer.Ordinal));
                        string note = string.IsNullOrWhiteSpace(assignment.DeckGroupEffectNote)
                            ? string.Empty
                            : $" · {assignment.DeckGroupEffectNote}";
                        return $"◆ {assignment.CharacterName}: {letters} · {assignment.DeckGroupConditionText}{note}";
                    }));

            stack.Children.Add(new Border
            {
                Background = BrushFromHex("#203B35"),
                BorderBrush = BrushFromHex("#4E9B82"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock
                {
                    Text = warningText,
                    Foreground = BrushFromHex("#BFF5DD"),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = featured ? 12 : 11
                }
            });
        }

        if (result.Assignments.Count > 0)
        {
            string assignments = string.Join(
                Environment.NewLine,
                result.Assignments.Select(FormatAssignmentText));

            stack.Children.Add(new TextBlock
            {
                Text = assignments,
                Foreground = BrushFromHex("#D7DEEA"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 21
            });
        }

        if (featured && result.ComboMatches.Count > 0)
        {
            string comboDetails = string.Join(
                Environment.NewLine,
                result.ComboMatches.Select(match =>
                    $"{match.StartIndex + 1}~{match.EndIndex + 1}칸 · {match.WordLength}글자 · {match.Word}"));

            stack.Children.Add(new Expander
            {
                Header = $"캐릭터 문자가 포함된 콤보 단어 {result.ComboMatches.Count}개 보기",
                Foreground = BrushFromHex("#B8EAF5"),
                Margin = new Thickness(0, 9, 0, 0),
                IsExpanded = false,
                Content = new TextBlock
                {
                    Text = comboDetails,
                    Foreground = BrushFromHex("#D7DEEA"),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                    Margin = new Thickness(0, 6, 0, 0)
                }
            });
        }

        card.Child = stack;
        return card;
    }

    private static string FormatAssignmentText(CharacterAssignment assignment)
    {
        if (assignment.IsGeneralSuggestion)
        {
            return $"{assignment.BoardIndex + 1}칸  {assignment.Letter}  ←  필요한 문자";
        }

        var tags = new List<string>();
        if (assignment.UsesAlternateForm)
        {
            tags.Add($"⇄ {assignment.CharacterFormName}");
        }
        if (assignment.UsesSpecialLetterState)
        {
            tags.Add($"⚠ {assignment.LetterStateName}");
        }
        if (assignment.UsesMiracleLeaderLetter)
        {
            tags.Add($"✨ {assignment.MiracleLeaderName} 리더 효과");
        }
        if (assignment.UsesDeckGroupConditionLetter)
        {
            tags.Add("◆ 덱 인원 조건");
        }

        string suffix = tags.Count == 0 ? string.Empty : $"  {string.Join(" · ", tags)}";
        return $"{assignment.BoardIndex + 1}칸  {assignment.Letter}  ←  {assignment.CharacterName}{suffix}";
    }

    private static string BuildAssignmentBoardLabel(CharacterAssignment assignment)
    {
        if (assignment.IsGeneralSuggestion)
        {
            return "필요 문자";
        }

        var labels = new List<string> { assignment.CharacterName };
        if (assignment.UsesAlternateForm)
        {
            labels.Add($"⇄ {assignment.CharacterFormName}");
        }
        if (assignment.UsesSpecialLetterState)
        {
            labels.Add($"⚠ {assignment.LetterStateName}");
        }
        if (assignment.UsesMiracleLeaderLetter)
        {
            labels.Add("✨ 리더 효과");
        }
        if (assignment.UsesDeckGroupConditionLetter)
        {
            labels.Add("◆ 덱 조건");
        }
        return string.Join(Environment.NewLine, labels);
    }

    private static string BuildAssignmentToolTip(int boardIndex, CharacterAssignment assignment)
    {
        if (assignment.IsGeneralSuggestion)
        {
            return $"{boardIndex + 1}칸 · 필요한 문자 {assignment.Letter} · 손패 제한 없는 일반 추천";
        }

        var lines = new List<string>
        {
            $"{boardIndex + 1}칸 · {assignment.Letter} · {assignment.CharacterName}"
        };
        if (!string.IsNullOrWhiteSpace(assignment.CharacterGroupName))
        {
            lines.Add($"소속 그룹: {assignment.CharacterGroupName}");
        }
        if (assignment.UsesAlternateForm)
        {
            lines.Add($"동일 이름 모드시프트: {assignment.CharacterFormName}");
        }
        if (assignment.UsesSpecialLetterState)
        {
            lines.Add($"문자 상태: {assignment.LetterStateName} ({assignment.LetterStateKind})");
            if (!string.IsNullOrWhiteSpace(assignment.LetterStateNote))
            {
                lines.Add(assignment.LetterStateNote);
            }
        }
        if (assignment.UsesMiracleLeaderLetter)
        {
            lines.Add($"미라클 문자: {assignment.MiracleLeaderName} 리더 효과");
            if (!string.IsNullOrWhiteSpace(assignment.MiracleEffectNote))
            {
                lines.Add(assignment.MiracleEffectNote);
            }
        }
        if (assignment.UsesDeckGroupConditionLetter)
        {
            lines.Add($"덱 조건 문자: {assignment.DeckGroupConditionText}");
            if (!string.IsNullOrWhiteSpace(assignment.DeckGroupEffectNote))
            {
                lines.Add(assignment.DeckGroupEffectNote);
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static Border CreateResultBadge(
        string text,
        string background,
        string border,
        bool featured,
        string? toolTip = null)
    {
        return new Border
        {
            Background = BrushFromHex(background),
            BorderBrush = BrushFromHex(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(5, 1, 0, 1),
            ToolTip = toolTip,
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = featured ? 12 : 10
            }
        };
    }

    private static (string Background, string Border) GetProbabilityColors(double rate)
    {
        return rate switch
        {
            >= 0.80 => ("#2C6B55", "#63C99A"),
            >= 0.50 => ("#53632D", "#A8C95A"),
            >= 0.20 => ("#6B4B2C", "#D69A55"),
            _ => ("#3A4250", "#687487")
        };
    }

    private FrameworkElement CreateWordCompositionPreview(SearchResult result, bool featured)
    {
        var container = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 2)
        };

        container.Children.Add(new TextBlock
        {
            Text = $"단어 구성 · {result.Cells.Count}글자",
            Foreground = BrushFromHex("#66D9EF"),
            FontWeight = FontWeights.SemiBold,
            FontSize = featured ? 12 : 11,
            Margin = new Thickness(2, 0, 0, 4)
        });

        var wordGrid = new UniformGrid
        {
            Columns = Math.Max(1, result.Cells.Count)
        };

        for (int offset = 0; offset < result.Cells.Count; offset++)
        {
            int boardIndex = result.StartIndex + offset;
            CharacterAssignment? assignment = result.Assignments
                .FirstOrDefault(item => item.BoardIndex == boardIndex);
            bool isPlaced = assignment is not null;
            string cell = result.Cells[offset];

            var slot = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            slot.Children.Add(new TextBlock
            {
                Text = cell,
                FontSize = featured ? 25 : 20,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            slot.Children.Add(new TextBlock
            {
                Text = isPlaced ? "이번 배치" : "기존 판면",
                FontSize = featured ? 10 : 9,
                Foreground = isPlaced
                    ? BrushFromHex("#E8F7FF")
                    : BrushFromHex("#AEB8C8"),
                TextAlignment = TextAlignment.Center,
                ToolTip = isPlaced
                    ? BuildAssignmentToolTip(boardIndex, assignment!)
                    : $"{boardIndex + 1}칸 · 기존 판면 문자 {cell}"
            });

            wordGrid.Children.Add(new Border
            {
                MinHeight = featured ? 61 : 52,
                Margin = new Thickness(2),
                Padding = new Thickness(3, 5, 3, 4),
                Background = isPlaced
                    ? BrushFromHex("#245E88")
                    : BrushFromHex("#2A3342"),
                BorderBrush = isPlaced
                    ? BrushFromHex("#66D9EF")
                    : BrushFromHex("#526176"),
                BorderThickness = new Thickness(isPlaced ? 2 : 1),
                CornerRadius = new CornerRadius(7),
                Child = slot
            });
        }

        container.Children.Add(wordGrid);
        container.Children.Add(new TextBlock
        {
            Text = "파랑: 이번에 놓을 문자 · 회색: 이미 있는 판면 문자",
            Foreground = BrushFromHex("#8FA0B4"),
            FontSize = 10,
            Margin = new Thickness(2, 3, 0, 0)
        });

        return container;
    }

    private FrameworkElement CreateFullBoardPreview(SearchResult result, bool featured)
    {
        var container = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 6)
        };

        var board = new UniformGrid
        {
            Columns = BoardSize
        };

        for (int index = 0; index < BoardSize; index++)
        {
            CharacterAssignment? assignment = result.Assignments
                .FirstOrDefault(item => item.BoardIndex == index);
            string? cell = index < result.CompletedBoard.Count
                ? result.CompletedBoard[index]
                : _boardCells[index];
            bool isPlaced = assignment is not null;
            bool isExisting = !isPlaced && !string.IsNullOrWhiteSpace(cell);

            var slotStack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            slotStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(cell) ? "□" : cell,
                FontSize = featured ? 24 : 19,
                FontWeight = FontWeights.Bold,
                Foreground = isPlaced
                    ? Brushes.White
                    : isExisting
                        ? BrushFromHex("#D7DEEA")
                        : BrushFromHex("#637083"),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            slotStack.Children.Add(new TextBlock
            {
                Text = isPlaced
                    ? BuildAssignmentBoardLabel(assignment!)
                    : isExisting ? "기존 판면" : "빈칸",
                FontSize = featured ? 10 : 9,
                Foreground = isPlaced
                    ? BrushFromHex("#E8F7FF")
                    : BrushFromHex("#9DA9BA"),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = isPlaced
                    ? BuildAssignmentToolTip(index, assignment!)
                    : $"{index + 1}칸 · {(isExisting ? "기존 판면 문자" : "빈칸")}"
            });

            board.Children.Add(new Border
            {
                MinHeight = featured ? 61 : 52,
                Margin = new Thickness(2),
                Padding = new Thickness(3, 5, 3, 4),
                Background = isPlaced
                    ? BrushFromHex("#245E88")
                    : isExisting
                        ? BrushFromHex("#2A3342")
                        : BrushFromHex("#171C26"),
                BorderBrush = isPlaced
                    ? BrushFromHex("#66D9EF")
                    : isExisting
                        ? BrushFromHex("#526176")
                        : BrushFromHex("#303A4A"),
                BorderThickness = new Thickness(isPlaced ? 2 : 1),
                CornerRadius = new CornerRadius(7),
                Child = slotStack
            });
        }

        container.Children.Add(board);
        container.Children.Add(new TextBlock
        {
            Text = "회색: 기존 판면 · 파랑: 이번에 배치할 문자",
            Foreground = BrushFromHex("#8FA0B4"),
            FontSize = 10,
            Margin = new Thickness(2, 3, 0, 0)
        });
        return container;
    }

    private void RenderInitialMessages()
    {
        _hasPerformedSearch = false;
        _lastGeneralResults = CreateEmptySearchGroups();
        _lastHandResults = CreateEmptySearchGroups();
        _lastDeckResults = CreateEmptySearchGroups();
        _lastGeneralSearchSignature = string.Empty;
        HandResultsTitleText.Text = "현재 손패로 가능";
        RenderGeneralSuggestionToggle();
        RenderInitialResultPanels();
    }

    private void RenderInitialResultPanels()
    {
        HandResultsPanel.Children.Clear();
        HandResultsPanel.Children.Add(new TextBlock
        {
            Text = "판면과 현재 손패를 선택하면 4~7글자 후보를 함께 검색합니다.",
            Foreground = BrushFromHex("#AEB8C8"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 4)
        });
        DeckResultsPanel.Children.Clear();
        DeckResultsPanel.Children.Add(new TextBlock
        {
            Text = "판면을 기준으로 덱 전체의 4~7글자 후보도 함께 표시됩니다.",
            Foreground = BrushFromHex("#AEB8C8"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 4, 2, 4)
        });
    }

    private void UpdateDictionaryInfoText()
    {
        string metadataPath = Path.Combine(_dataDirectory, "gaccag_update.json");
        GaccagUpdateMetadata? metadata = JsonDataLoader.LoadGaccagMetadata(metadataPath);

        if (metadata is not null)
        {
            int searchCount = metadata.SearchWordCount > 0
                ? metadata.SearchWordCount
                : _loadedGaccagSearchWordCount;
            int shortCount = metadata.ShortWordCount > 0
                ? metadata.ShortWordCount
                : _loadedGaccagShortWordCount;

            string partialLabel = metadata.IsPartial ? " · 중간 저장됨" : string.Empty;
            DictionaryUpdateText.Text = shortCount > 0
                ? $"GACCAG 전체 {metadata.WordCount:N0}개 · 검색용 4~7글자 {searchCount:N0}개 · " +
                  $"콤보용 2~3글자 {shortCount:N0}개 · 마지막 업데이트 " +
                  $"{metadata.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}{partialLabel}"
                : $"GACCAG {metadata.WordCount:N0}개 · 마지막 업데이트 " +
                  $"{metadata.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}{partialLabel}";
            return;
        }

        string gaccagWordsPath = GetExistingGaccagWordsPath();
        if (File.Exists(gaccagWordsPath) && _loadedGaccagWordCount > 0)
        {
            DateTime updatedAt = File.GetLastWriteTime(gaccagWordsPath);
            DictionaryUpdateText.Text =
                _loadedGaccagShortWordCount > 0
                    ? $"GACCAG 전체 {_loadedGaccagWordCount:N0}개 · 검색용 4~7글자 " +
                      $"{_loadedGaccagSearchWordCount:N0}개 · 콤보용 2~3글자 " +
                      $"{_loadedGaccagShortWordCount:N0}개 · 파일 수정 {updatedAt:yyyy-MM-dd HH:mm}"
                    : $"GACCAG {_loadedGaccagWordCount:N0}개 · 파일 수정 {updatedAt:yyyy-MM-dd HH:mm}";
            return;
        }

        DictionaryUpdateText.Text =
            "아직 GACCAG 데이터를 내려받지 않았습니다. 오른쪽 업데이트 버튼을 누르면 자동 수집합니다.";
    }

    private void OpenGaccagSiteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = GaccagDictionaryImporter.SourceUrl,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"GACCAG 사이트를 열지 못했습니다.\n\n{exception.Message}",
                "사이트 열기 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void UpdateSevenLetterButton_Click(object sender, RoutedEventArgs e)
        => await RunGaccagUpdateAsync(GaccagImportMode.SevenLetters);

    private async void UpdateSixLetterButton_Click(object sender, RoutedEventArgs e)
        => await RunGaccagUpdateAsync(GaccagImportMode.SixLetters);

    private async void UpdateFiveLetterButton_Click(object sender, RoutedEventArgs e)
        => await RunGaccagUpdateAsync(GaccagImportMode.FiveLetters);

    private async void UpdateFourLetterButton_Click(object sender, RoutedEventArgs e)
        => await RunGaccagUpdateAsync(GaccagImportMode.FourLetters);

    private async void UpdateSearchWordsButton_Click(object sender, RoutedEventArgs e)
        => await RunGaccagUpdateAsync(GaccagImportMode.SearchWords);

    private async void UpdateComboWordsButton_Click(object sender, RoutedEventArgs e)
        => await RunGaccagUpdateAsync(GaccagImportMode.ComboWords);

    private async Task RunGaccagUpdateAsync(GaccagImportMode mode)
    {
        if (_isUpdatingDictionary)
        {
            return;
        }

        string modeLabel = GaccagDictionaryImporter.GetModeLabel(mode);
        _isUpdatingDictionary = true;
        SetDictionaryUpdateButtonsEnabled(false);
        CancelGaccagUpdateButton.IsEnabled = true;
        CancelGaccagUpdateButton.Visibility = Visibility.Visible;
        DictionaryUpdateProgressBar.Visibility = Visibility.Visible;
        DictionaryUpdateProgressBar.IsIndeterminate = true;
        DictionaryUpdateProgressText.Visibility = Visibility.Visible;
        DictionaryUpdateProgressText.Text =
            $"{modeLabel} 준비 중 · 직접 요청 가능 여부를 확인합니다.";
        StatusText.Text = $"GACCAG {modeLabel} 진행 중";

        _dictionaryUpdateCts = new CancellationTokenSource();

        var progress = new Progress<GaccagImportProgress>(value =>
        {
            DictionaryUpdateProgressText.Text =
                $"{value.Message} · 전체 {value.CollectedWords:N0}개 · 새로 추가 {value.AddedWords:N0}개";

            if (value.EstimatedQueries is > 0)
            {
                DictionaryUpdateProgressBar.IsIndeterminate = false;
                DictionaryUpdateProgressBar.Maximum = value.EstimatedQueries.Value;
                DictionaryUpdateProgressBar.Value = Math.Min(
                    value.CompletedQueries,
                    value.EstimatedQueries.Value);
            }
        });

        try
        {
            string destinationPath = Path.Combine(_dataDirectory, "gaccag_words.json.gz");
            string metadataPath = Path.Combine(_dataDirectory, "gaccag_update.json");
            string checkpointPath = Path.Combine(
                _dataDirectory,
                $"gaccag_checkpoint_{mode}.json");
            var importer = new GaccagDictionaryImporter();

            GaccagImportResult result = await importer.ImportAsync(
                destinationPath,
                metadataPath,
                checkpointPath,
                mode,
                progress,
                _dictionaryUpdateCts.Token);

            RefreshAfterDictionaryUpdate();

            DictionaryUpdateProgressBar.IsIndeterminate = false;
            DictionaryUpdateProgressBar.Maximum = 1;
            DictionaryUpdateProgressBar.Value = 1;
            DictionaryUpdateProgressText.Text =
                $"완료: 새 단어 {result.AddedWordCount:N0}개 추가 · GACCAG 전체 " +
                $"{result.WordCount:N0}개 (검색용 {result.SearchWordCount:N0}개 · " +
                $"콤보용 {result.ShortWordCount:N0}개)";
            StatusText.Text =
                $"{modeLabel} 완료 · 전체 단어 {_loadedWordCount:N0}개";
        }
        catch (OperationCanceledException)
        {
            RefreshAfterDictionaryUpdate();
            DictionaryUpdateProgressText.Text =
                "업데이트를 중단했습니다. 지금까지 찾은 단어는 저장되었고, 같은 버튼을 누르면 이어서 진행합니다.";
            StatusText.Text = "GACCAG 업데이트 중단 · 중간 데이터 저장 완료";
        }
        catch (Exception exception)
        {
            RefreshAfterDictionaryUpdate();
            DictionaryUpdateProgressBar.IsIndeterminate = false;
            DictionaryUpdateProgressBar.Value = 0;
            DictionaryUpdateProgressText.Text =
                "오류가 발생했지만 지금까지 수집한 데이터는 중간 저장했습니다.";
            StatusText.Text = "GACCAG 업데이트 오류 · 중간 데이터 보존됨";

            MessageBox.Show(
                $"GACCAG 단어 데이터를 가져오는 중 오류가 발생했습니다.\n\n{exception.Message}\n\n지금까지 수집한 단어는 저장되어 있습니다.",
                "GACCAG 업데이트 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _dictionaryUpdateCts?.Dispose();
            _dictionaryUpdateCts = null;
            _isUpdatingDictionary = false;
            SetDictionaryUpdateButtonsEnabled(true);
            CancelGaccagUpdateButton.IsEnabled = true;
            CancelGaccagUpdateButton.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshAfterDictionaryUpdate()
    {
        LoadDataFromDisk();
        UpdateCharacterButtons();
        RenderSelectedHandSlots();
        PerformSearch(isAutomatic: false);
    }

    private void SetDictionaryUpdateButtonsEnabled(bool isEnabled)
    {
        UpdateSevenLetterButton.IsEnabled = isEnabled;
        UpdateSixLetterButton.IsEnabled = isEnabled;
        UpdateFiveLetterButton.IsEnabled = isEnabled;
        UpdateFourLetterButton.IsEnabled = isEnabled;
        UpdateSearchWordsButton.IsEnabled = isEnabled;
        UpdateComboWordsButton.IsEnabled = isEnabled;
    }

    private void CancelGaccagUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingDictionary || _dictionaryUpdateCts is null)
        {
            return;
        }

        CancelGaccagUpdateButton.IsEnabled = false;
        DictionaryUpdateProgressText.Text =
            "현재 검색이 끝나는 즉시 중단하고, 지금까지 찾은 단어와 진행 위치를 저장합니다.";
        StatusText.Text = "GACCAG 업데이트 중단을 요청했습니다.";
        _dictionaryUpdateCts.Cancel();
    }

    private void BackupRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        _autoSearchTimer.Stop();
        _settingsSaveTimer.Stop();
        SaveSettingsImmediatelySafely();

        var window = new DataBackupWindow(_dataDirectory)
        {
            Owner = this
        };

        window.ShowDialog();
        if (!window.RestoreCompleted)
        {
            ScheduleAutoSearch();
            return;
        }

        try
        {
            UserSettingsService.InvalidateCache();
            CharacterImageService.ClearThumbnailCache();
            AppPaths.SynchronizeBundledData(forceRestoreMissingBundledCharacters: true);

            _selectedHandCharacterIds.Clear();
            _selectedHandLetterStateIds.Clear();
            _selectedHandFormIds.Clear();
            _boardHistory.Clear();
            Array.Clear(_boardCells, 0, _boardCells.Length);
            _activeBoardIndex = 0;

            LoadDataFromDisk();
            RestoreSettings();
            UpdateBoardButtons();
            UpdateCharacterButtons();
            RenderSelectedHandSlots();
            PerformSearch(isAutomatic: false);

            StatusText.Text =
                $"백업 복원 완료 · 단어 {_loadedWordCount:N0}개 · 덱 캐릭터 {_deck.Count:N0}명";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"백업 파일은 복원됐지만 화면을 다시 읽는 중 오류가 발생했습니다.\n" +
                $"프로그램을 한 번 다시 실행해 주세요.\n\n{exception.Message}",
                "복원 후 새로고침 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _dataDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            AppLog.Warning("사용자 데이터 폴더를 열지 못했습니다.", exception);
            MessageBox.Show(
                $"사용자 데이터 폴더를 열지 못했습니다.\n\n{exception.Message}",
                "폴더 열기 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            AppLog.Warning("로그 폴더를 열지 못했습니다.", exception);
            MessageBox.Show(
                $"로그 폴더를 열지 못했습니다.\n\n{exception.Message}",
                "폴더 열기 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ReloadDataButton_Click(object sender, RoutedEventArgs e)
    {
        _autoSearchTimer.Stop();

        try
        {
            LoadDataFromDisk();
            UpdateCharacterButtons();
            RenderSelectedHandSlots();
            PerformSearch(isAutomatic: false);
            StatusText.Text =
                $"데이터를 다시 읽었습니다. 단어 {_loadedWordCount:N0}개 · 덱 캐릭터 {_deck.Count:N0}명";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"데이터를 다시 읽는 중 오류가 발생했습니다.\n\n{exception.Message}",
                "데이터 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        _autoSearchTimer.Stop();
        PerformSearch(isAutomatic: false);
    }

    private void AutoSearchCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        if (AutoSearchCheckBox.IsChecked == true)
        {
            StatusText.Text = "자동 검색을 켰습니다.";
            ScheduleAutoSearch();
        }
        else
        {
            _autoSearchTimer.Stop();
            StatusText.Text = "자동 검색을 껐습니다. 필요할 때 '지금 검색'을 누르세요.";
        }

        SaveSettingsSafely();
    }

    private void PreviousSlotButton_Click(object sender, RoutedEventArgs e)
        => MoveActiveSlot(-1);

    private void NextSlotButton_Click(object sender, RoutedEventArgs e)
        => MoveActiveSlot(1);

    private void UndoBoardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boardHistory.Count == 0)
        {
            StatusText.Text = "되돌릴 판면 입력이 없습니다.";
            return;
        }

        string?[] previous = _boardHistory.Pop();
        Array.Copy(previous, _boardCells, BoardSize);
        UpdateBoardButtons();
        SaveSettingsSafely();
        ScheduleAutoSearch();
        StatusText.Text = "직전 판면 입력을 되돌렸습니다.";
    }

    private void ClearBoardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_boardCells.All(cell => cell is null))
        {
            return;
        }

        PushBoardHistory();
        Array.Clear(_boardCells, 0, _boardCells.Length);
        _activeBoardIndex = 0;
        UpdateBoardButtons();
        SaveSettingsSafely();
        ScheduleAutoSearch();
        StatusText.Text = "판면을 초기화했습니다.";
    }


    private void RefreshMainPresetList()
    {
        string presetPath = Path.Combine(_dataDirectory, "deck_presets.json");
        _mainPresets = DeckPresetService.LoadOrCreate(presetPath, _deck)
            .Select(DeckPresetService.Clone)
            .ToArray();

        _isRefreshingMainPresets = true;
        try
        {
            MainPresetDisplayItem[] items = _mainPresets
                .OrderBy(preset => preset.Name, StringComparer.Ordinal)
                .ThenBy(preset => preset.Id, StringComparer.Ordinal)
                .Select(preset => new MainPresetDisplayItem(preset))
                .ToArray();
            MainPresetComboBox.ItemsSource = items;

            string[] currentDeckIds = _deck.Select(character => character.Id).ToArray();
            MainPresetDisplayItem? exactMatch = items.FirstOrDefault(item =>
            {
                DeckPreset? preset = _mainPresets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, item.Id, StringComparison.Ordinal));
                return preset is not null &&
                       preset.CharacterIds.SequenceEqual(currentDeckIds, StringComparer.Ordinal);
            });

            MainPresetDisplayItem? selected = exactMatch ?? items.FirstOrDefault(item =>
                string.Equals(item.Id, _selectedMainPresetId, StringComparison.Ordinal));
            MainPresetComboBox.SelectedItem = selected;
            _selectedMainPresetId = selected?.Id;

            MainPresetStatusText.Text = exactMatch is not null
                ? $"현재 적용 중: {exactMatch.Name}"
                : items.Length == 0
                    ? "저장된 덱 프리셋이 없습니다. 덱 편집 화면에서 먼저 만들어 주세요."
                    : "프리셋을 선택한 뒤 '이 덱으로 변경'을 누르세요.";
            MainPresetStatusText.Foreground = exactMatch is not null
                ? BrushFromHex("#8FE3B1")
                : BrushFromHex("#8FA0B4");
        }
        finally
        {
            _isRefreshingMainPresets = false;
        }
    }

    private void MainPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingMainPresets || MainPresetComboBox.SelectedItem is not MainPresetDisplayItem item)
        {
            return;
        }

        _selectedMainPresetId = item.Id;
        MainPresetStatusText.Text = $"'{item.Name}' 선택 · 버튼을 누르면 현재 덱과 손패 후보가 갱신됩니다.";
        MainPresetStatusText.Foreground = BrushFromHex("#B8EAF5");
    }

    private void ApplyMainPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingDictionary)
        {
            StatusText.Text = "단어 데이터 업데이트가 끝난 뒤 덱 프리셋을 변경하세요.";
            return;
        }

        DeckPreset? preset = _mainPresets.FirstOrDefault(item =>
            string.Equals(item.Id, _selectedMainPresetId, StringComparison.Ordinal));
        if (preset is null)
        {
            MainPresetStatusText.Text = "변경할 덱 프리셋을 먼저 선택하세요.";
            MainPresetStatusText.Foreground = BrushFromHex("#FF9E9E");
            return;
        }

        try
        {
            _autoSearchTimer.Stop();
            string deckPath = Path.Combine(_dataDirectory, "deck.json");
            string libraryPath = Path.Combine(_dataDirectory, "characters.json");
            IReadOnlyList<CharacterEntry> library = CharacterLibraryService.LoadOrCreate(libraryPath, _deck);
            Dictionary<string, CharacterEntry> charactersById = library
                .Where(character => !string.IsNullOrWhiteSpace(character.Id))
                .GroupBy(character => character.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var deckCharacters = new List<CharacterEntry>();
            var usedRestrictionGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int missingCount = 0;
            int restrictedCount = 0;

            foreach (string characterId in preset.CharacterIds.Distinct(StringComparer.Ordinal).Take(12))
            {
                if (!charactersById.TryGetValue(characterId, out CharacterEntry? character))
                {
                    missingCount++;
                    continue;
                }

                string restrictionGroup = (character.DeckRestrictionGroupId ?? string.Empty).Trim();
                if (restrictionGroup.Length > 0 && !usedRestrictionGroups.Add(restrictionGroup))
                {
                    restrictedCount++;
                    continue;
                }

                deckCharacters.Add(CharacterLibraryService.Clone(character));
            }

            if (deckCharacters.Count == 0)
            {
                throw new InvalidOperationException("이 프리셋에서 불러올 수 있는 캐릭터가 없습니다.");
            }

            DeckDataService.Save(deckPath, deckCharacters);
            LoadDataFromDisk();
            UpdateCharacterButtons();
            RenderSelectedHandSlots();
            PerformSearch(isAutomatic: false);
            SaveSettingsSafely();

            string note = missingCount + restrictedCount > 0
                ? $" · 누락 {missingCount}명 · 모드시프트 중복 제외 {restrictedCount}명"
                : string.Empty;
            MainPresetStatusText.Text = $"현재 적용 중: {preset.Name}{note}";
            MainPresetStatusText.Foreground = BrushFromHex("#8FE3B1");
            StatusText.Text = $"'{preset.Name}' 프리셋으로 덱 {_deck.Count:N0}명을 변경했습니다.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"덱 프리셋을 적용하지 못했습니다.\n\n{exception.Message}",
                "프리셋 적용 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ScheduleAutoSearch();
        }
    }

    private void EditDeckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingDictionary)
        {
            StatusText.Text = "단어 데이터 업데이트가 끝난 뒤 덱을 편집하세요.";
            return;
        }

        _autoSearchTimer.Stop();
        IReadOnlyList<CharacterEntry> previousDeck = _deck
            .Select(CharacterLibraryService.Clone)
            .ToArray();
        string deckPath = Path.Combine(_dataDirectory, "deck.json");
        string libraryPath = Path.Combine(_dataDirectory, "characters.json");
        var editor = new DeckEditorWindow(deckPath, libraryPath, _deck)
        {
            Owner = this
        };

        bool? editorResult = editor.ShowDialog();
        if (editorResult != true && !editor.HasSavedToDisk)
        {
            ScheduleAutoSearch();
            return;
        }

        try
        {
            // 연결형 모드시프트는 캐릭터 ID가 바뀌므로 디스크를 다시 읽기 전에
            // 편집 전/후의 같은 덱 슬롯을 비교해 현재 손패 ID도 새 형태로 치환합니다.
            // 동일명 모드시프트는 SavedCharacters의 ActiveFormId를 사용해 손패 형태를 동기화합니다.
            SynchronizeSelectedHandAfterDeckEdit(previousDeck, editor.SavedCharacters);
            _modeShiftLibraryCache = null;

            // 덱 편집으로 단어 사전 자체가 바뀐 것은 아니므로 21만+ 단어 DB를
            // 다시 읽고 검색 인덱스를 재생성하지 않습니다. 덱 12명만 빠르게 갱신합니다.
            ReloadDeckOnlyFromDisk();
            UpdateCharacterButtons();
            RenderSelectedHandSlots();
            PerformSearch(isAutomatic: false);
            StatusText.Text = $"덱 캐릭터 {_deck.Count:N0}명을 저장하고 검색에 반영했습니다.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"저장된 덱 데이터를 다시 읽지 못했습니다.\n\n{exception.Message}",
                "덱 다시 읽기 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void SynchronizeSelectedHandAfterDeckEdit(
        IReadOnlyList<CharacterEntry> previousDeck,
        IReadOnlyList<CharacterEntry> editedDeck)
    {
        if (_selectedHandCharacterIds.Count == 0 || editedDeck.Count == 0)
        {
            return;
        }

        var editedById = editedDeck
            .Where(character => !string.IsNullOrWhiteSpace(character.Id))
            .GroupBy(character => character.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        for (int handIndex = 0; handIndex < _selectedHandCharacterIds.Count; handIndex++)
        {
            string oldHandId = _selectedHandCharacterIds[handIndex];
            int previousDeckIndex = -1;
            for (int index = 0; index < previousDeck.Count; index++)
            {
                if (string.Equals(previousDeck[index].Id, oldHandId, StringComparison.Ordinal))
                {
                    previousDeckIndex = index;
                    break;
                }
            }

            string synchronizedId = oldHandId;
            if (previousDeckIndex >= 0 && previousDeckIndex < editedDeck.Count)
            {
                CharacterEntry previousCharacter = previousDeck[previousDeckIndex];
                CharacterEntry editedSlotCharacter = editedDeck[previousDeckIndex];
                if (!string.Equals(previousCharacter.Id, editedSlotCharacter.Id, StringComparison.Ordinal) &&
                    AreConnectedModeShiftMembers(previousCharacter, editedSlotCharacter))
                {
                    ReplaceSelectedHandCharacter(handIndex, oldHandId, editedSlotCharacter);
                    synchronizedId = editedSlotCharacter.Id;
                }
            }

            if (!editedById.TryGetValue(synchronizedId, out CharacterEntry? editedCharacter))
            {
                continue;
            }

            // 덱 편집기에서 2.2초 롱프레스로 선택한 동일명 형태도 현재 손패가 바로 따라갑니다.
            _selectedHandFormIds.Remove(synchronizedId);
            if (editedCharacter.FindForm(editedCharacter.ActiveFormId) is not null)
            {
                _selectedHandFormIds[synchronizedId] = editedCharacter.ActiveFormId!;
            }
        }
    }

    private static bool AreConnectedModeShiftMembers(CharacterEntry left, CharacterEntry right)
    {
        string leftGroup = NormalizeModeShiftGroup(left.DeckRestrictionGroupId);
        string rightGroup = NormalizeModeShiftGroup(right.DeckRestrictionGroupId);
        return leftGroup.Length > 0 &&
               string.Equals(leftGroup, rightGroup, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearHandButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedHandCharacterIds.Count == 0)
        {
            return;
        }

        string[] previousSelection = _selectedHandCharacterIds.ToArray();
        _selectedHandCharacterIds.Clear();
        _selectedHandLetterStateIds.Clear();
        _selectedHandFormIds.Clear();
        UpdateCharacterButtons(previousSelection);
        RenderSelectedHandSlots();
        SaveSettingsSafely();
        ScheduleAutoSearch(handChanged: true);
        StatusText.Text = "현재 손패를 초기화했습니다.";
    }

    private void DeckResultSortComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (DeckResultSortComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _deckResultSortMode = NormalizeDeckResultSortMode(item.Tag?.ToString());
        if (_isInitializing)
        {
            return;
        }

        if (_hasPerformedSearch)
        {
            RenderStoredSearchResults();
        }

        SaveSettingsSafely();
        StatusText.Text = $"덱 전체 결과를 {GetDeckSortDescription()}으로 정렬했습니다.";
    }

    private static string NormalizeDeckResultSortMode(string? value)
    {
        return value switch
        {
            "Probability" => "Probability",
            "Combo" => "Combo",
            _ => "Practical"
        };
    }

    private string GetDeckSortDescription()
    {
        return _deckResultSortMode switch
        {
            "Probability" => "첫 턴 성립률 높은 순",
            "Combo" => "예상 콤보 높은 순",
            _ => "실전 추천 순"
        };
    }

    private void ApplyQuickBoardInputButton_Click(object sender, RoutedEventArgs e)
        => ApplyQuickBoardInput();

    private void QuickBoardInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        ApplyQuickBoardInput();
        e.Handled = true;
    }

    private void QuickBoardInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string text = QuickBoardInputTextBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            BoardHangulPreviewText.Text = "빠른 입력에 한글을 입력하면 가나로 자동 변환됩니다.";
            BoardHangulPreviewText.Foreground = BrushFromHex("#718096");
            return;
        }

        string converted = KanaUtility.ConvertHangulToKana(text);
        try
        {
            IReadOnlyList<string?> cells = ParseQuickBoardInput(text);
            string conversionText = converted != text
                ? $"가나 변환: {text} → {converted}"
                : $"입력 결과: {converted}";
            BoardHangulPreviewText.Text =
                $"{conversionText} · 판면 {cells.Count}/{BoardSize}칸";
            BoardHangulPreviewText.Foreground = BrushFromHex("#8FD8E8");
        }
        catch (FormatException exception)
        {
            BoardHangulPreviewText.Text =
                $"입력 확인: {converted} · {exception.Message}";
            BoardHangulPreviewText.Foreground = BrushFromHex("#FFD08A");
        }
    }

    private void ApplyQuickBoardInput()
    {
        IReadOnlyList<string?> parsedCells;
        try
        {
            parsedCells = ParseQuickBoardInput(QuickBoardInputTextBox.Text);
        }
        catch (FormatException exception)
        {
            MessageBox.Show(
                exception.Message,
                "빠른 입력 확인",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        PushBoardHistory();
        for (int index = 0; index < BoardSize; index++)
        {
            _boardCells[index] = index < parsedCells.Count
                ? parsedCells[index]
                : null;
        }

        _activeBoardIndex = Math.Min(parsedCells.Count, BoardSize - 1);
        UpdateBoardButtons();
        SaveSettingsSafely();
        ScheduleAutoSearch();
        StatusText.Text = "빠른 입력을 판면에 적용했습니다.";
    }

    private static IReadOnlyList<string?> ParseQuickBoardInput(string text)
    {
        var result = new List<string?>();
        string normalized = KanaUtility.ConvertHangulToKana(text ?? string.Empty)
            .Normalize(NormalizationForm.FormC);

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            string value = rune.ToString();

            if (value is "," or "/" or "|")
            {
                continue;
            }

            if (value is "_" or "□" or "・" or "." or "-" or "?" or "？" or " " or "　")
            {
                // 스페이스(반각/전각)도 밑줄・네모와 동일하게 빈칸으로 인식합니다.
                result.Add(null);
            }
            else if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }
            else
            {
                string cell = KanaUtility.NormalizeCell(value);
                if (!KanaUtility.IsJapaneseCell(cell))
                {
                    throw new FormatException(
                        $"'{value}' 문자는 가나로 변환할 수 없습니다.");
                }

                result.Add(cell);
            }

            if (result.Count > BoardSize)
            {
                throw new FormatException(
                    "판면은 최대 7칸입니다. 띄어쓰기·'_'·'□'도 빈칸 1칸으로 계산됩니다.");
            }
        }

        if (result.Count == 0)
        {
            throw new FormatException(
                "입력된 문자가 없습니다. 예: _ゅう_くせい");
        }

        return result;
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            return;
        }

        IReadOnlyList<string> cells = KanaUtility.SplitIntoCells(e.Text);
        if (cells.Count != 1 || !KanaUtility.IsJapaneseCell(cells[0]))
        {
            return;
        }

        SetActiveBoardCell(cells[0], advance: true);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
                MoveActiveSlot(-1);
                e.Handled = true;
                break;
            case Key.Right:
                MoveActiveSlot(1);
                e.Handled = true;
                break;
            case Key.Back:
            case Key.Delete:
                SetActiveBoardCell(null, advance: false);
                e.Handled = true;
                break;
            case Key.Space:
                SetActiveBoardCell(null, advance: true);
                e.Handled = true;
                break;
            case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                UndoBoardButton_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void SaveSettingsSafely()
    {
        if (_isInitializing)
        {
            return;
        }

        // 연속 입력/클릭 때마다 디스크를 쓰지 않고 마지막 변경 후 한 번만 저장합니다.
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SaveSettingsImmediatelySafely()
    {
        try
        {
            SaveSettings();
        }
        catch
        {
            // 설정 저장 실패가 검색과 입력을 막지 않도록 합니다.
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _dictionaryUpdateCts?.Cancel();
        _autoSearchTimer.Stop();
        _settingsSaveTimer.Stop();
        _handModeShiftHoldTimer.Stop();
        SaveSettingsImmediatelySafely();
    }

    private static SolidColorBrush BrushFromHex(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));

    private sealed class GeneralSuggestionTableRow
    {
        public int Number { get; init; }
        public string Word { get; init; } = string.Empty;
        public string RequiredLetters { get; init; } = string.Empty;
        public string Placement { get; init; } = string.Empty;
        public int ComboCount { get; init; }
    }

    private sealed record KanaRow
    {
        public KanaRow(string label, string letters)
        {
            Label = label;
            Letters = letters
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        public string Label { get; }
        public IReadOnlyList<string> Letters { get; }
    }

    private sealed class MainPresetDisplayItem
    {
        public MainPresetDisplayItem(DeckPreset preset)
        {
            Id = preset.Id;
            Name = preset.Name;
            DisplayText = $"{preset.Name}  ({preset.CharacterIds.Count}명)";
        }

        public string Id { get; }
        public string Name { get; }
        public string DisplayText { get; }

        public override string ToString() => DisplayText;
    }

    private sealed class FormOptionItem
    {
        public FormOptionItem(
            string id,
            string name,
            string imageFileName,
            IEnumerable<string> letters)
        {
            Id = id;
            Name = name;
            ImageFileName = imageFileName ?? string.Empty;
            Letters = (letters ?? Array.Empty<string>()).ToArray();
            DisplayText = $"{name} · {string.Join(" · ", Letters)}";
        }

        public string Id { get; }
        public string Name { get; }
        public string ImageFileName { get; }
        public IReadOnlyList<string> Letters { get; }
        public string DisplayText { get; }
    }

    private sealed class LetterStateOptionItem
    {
        public LetterStateOptionItem(
            string id,
            string name,
            string kind,
            string note,
            bool isSpecial)
        {
            Id = id;
            Name = name;
            Kind = kind;
            Note = note;
            IsSpecial = isSpecial;
            DisplayText = isSpecial ? $"{name} · {kind}" : name;
        }

        public string Id { get; }
        public string Name { get; }
        public string Kind { get; }
        public string Note { get; }
        public bool IsSpecial { get; }
        public string DisplayText { get; }
    }

}
