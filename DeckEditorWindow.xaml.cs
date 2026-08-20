using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Services;
using KotodamanWordFinder.Utilities;
using Microsoft.Win32;

namespace KotodamanWordFinder;

public partial class DeckEditorWindow : Window
{
    private const int MaximumDeckSize = 12;
    private const int LibraryPageSize = 60;

    private static readonly string[] AttributeValues = { "火", "水", "木", "光", "闇", "天", "冥", "虹" };
    private static readonly string[] SpeciesValues = { "神", "魔", "英", "龍", "獣", "霊", "物", "妖" };

    private readonly string _deckPath;
    private readonly string _libraryPath;
    private readonly string _presetPath;
    private readonly string _dataDirectory;
    private readonly List<CharacterEntry> _library;
    private readonly List<string> _deckIds;
    private readonly List<DeckPreset> _presets;
    private readonly Dictionary<string, ImageSource?> _thumbnailCache =
        new(StringComparer.Ordinal);
    private readonly object _thumbnailCacheLock = new();
    private readonly Dictionary<string, string> _listSummaryCache =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CharacterEntry> _characterById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _searchCandidateCache =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, CharacterEntry[]> _relatedFormsCache =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _effectiveGroupCache =
        new(StringComparer.Ordinal);
    private List<CharacterLetterState> _editingLetterStates = new();
    private List<CharacterForm> _editingForms = new();
    private string _editingImageFileName = string.Empty;
    private string? _pendingImageSourcePath;
    private bool _removeImageRequested;

    private string? _editingCharacterId;
    private string? _selectedPresetId;
    private bool _isRefreshingLists;
    private bool _isRefreshingPresets;
    private bool _isRelatedFormEditorOpen;
    private bool _isRefreshingGroupFilter;
    private bool _groupFilterOptionsDirty = true;
    private bool _hasSavedToDisk;
    private bool _autoSaveReady;
    private bool _isPersistingState;
    private readonly DispatcherTimer _autoSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(650)
    };
    private readonly DispatcherTimer _librarySearchTimer = new()
    {
        // 한글 IME는 한 글자를 완성하는 동안 TextChanged가 여러 번 발생할 수 있습니다.
        // 입력이 잠깐 멈춘 뒤 한 번만 전체 목록을 필터링해 UI 멈춤을 방지합니다.
        Interval = TimeSpan.FromMilliseconds(220)
    };
    private readonly DispatcherTimer _libraryModeShiftHoldTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(2200)
    };
    private readonly DispatcherTimer _deckModeShiftHoldTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(2200)
    };
    private string? _pendingLibraryModeShiftCharacterId;
    private int _pendingDeckModeShiftIndex = -1;
    private bool _modeShiftHoldTriggered;
    private bool _hasPendingAutoSave;
    // 마지막 디스크 저장 시도가 성공했는지 추적합니다.
    // 편집/덱 변경은 이미 즉시 자동 저장되므로 창을 닫을 때 같은 2,500명 데이터를 다시 쓰지 않습니다.
    private bool _lastPersistSucceeded = true;
    private bool _isClosingAfterExplicitSave;
    private string? _lastSelectedCharacterId;
    private int _libraryPageIndex;
    private int _lastFilteredLibraryCount;
    private int _libraryPageCount = 1;

    public DeckEditorWindow(
        string deckPath,
        string libraryPath,
        IReadOnlyList<CharacterEntry> currentDeck)
    {
        InitializeComponent();
        Title = $"{Title} v{AppPaths.AppVersion}";

        SourceInitialized += (_, _) => FitWindowToCurrentWorkArea();
        Closing += DeckEditorWindow_Closing;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
        _librarySearchTimer.Tick += LibrarySearchTimer_Tick;
        _libraryModeShiftHoldTimer.Tick += LibraryModeShiftHoldTimer_Tick;
        _deckModeShiftHoldTimer.Tick += DeckModeShiftHoldTimer_Tick;
        Closed += (_, _) =>
        {
            _autoSaveTimer.Stop();
            _librarySearchTimer.Stop();
            _libraryModeShiftHoldTimer.Stop();
            _deckModeShiftHoldTimer.Stop();
            SaveDeckEditorFilterStateSafely();
        };

        CharacterCategoryComboBox.ItemsSource = CharacterCategories.All;
        CharacterCategoryComboBox.SelectedItem = CharacterCategories.Other;
        CategoryFilterComboBox.ItemsSource = new[] { "전체 등급" }
            .Concat(CharacterCategories.All)
            .ToArray();
        CategoryFilterComboBox.SelectedIndex = 0;
        CharacterAttributeComboBox.ItemsSource = new[] { "미입력" }.Concat(AttributeValues).ToArray();
        CharacterAttributeComboBox.SelectedItem = "미입력";
        CharacterSpeciesComboBox.ItemsSource = new[] { "미입력" }.Concat(SpeciesValues).ToArray();
        CharacterSpeciesComboBox.SelectedItem = "미입력";
        AttributeFilterComboBox.ItemsSource = new[] { "전체 속성" }.Concat(AttributeValues).Concat(new[] { "미입력" }).ToArray();
        AttributeFilterComboBox.SelectedIndex = 0;
        SpeciesFilterComboBox.ItemsSource = new[] { "전체 종족" }.Concat(SpeciesValues).Concat(new[] { "미입력" }).ToArray();
        SpeciesFilterComboBox.SelectedIndex = 0;
        StatusFilterComboBox.ItemsSource = new[]
        {
            "전체 상태",
            "현재 덱",
            "현재 덱 제외",
            "이미지 없음",
            "그룹 없음",
            "동일명 모드시프트",
            "이름 다른 모드시프트",
            "조건 문자 보유",
            "미라클 효과 보유",
            "덱 그룹 조건 문자 보유",
            "미완성 정보"
        };
        StatusFilterComboBox.SelectedIndex = 0;
        SortComboBox.ItemsSource = new[]
        {
            "기본 정렬",
            "이름순",
            "속성순",
            "종족순",
            "현재 덱 우선",
            "이미지 없음 우선",
            "그룹 없음 우선"
        };
        SortComboBox.SelectedIndex = 0;

        _deckPath = deckPath;
        _libraryPath = libraryPath;
        _dataDirectory = Path.GetDirectoryName(deckPath) ?? Directory.GetCurrentDirectory();
        _presetPath = Path.Combine(
            Path.GetDirectoryName(deckPath) ?? Directory.GetCurrentDirectory(),
            "deck_presets.json");
        // LoadOrCreate 자체가 편집용 복제본을 반환하므로 여기서 전 캐릭터를 한 번 더 Clone하지 않습니다.
        // 캐릭터가 2,500명 이상으로 늘어날 때 창을 여는 초기 할당량을 크게 줄입니다.
        _library = CharacterLibraryService
            .LoadOrCreate(libraryPath, currentDeck)
            .ToList();
        _deckIds = currentDeck
            .Select(character => character.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumDeckSize)
            .ToList();
        _presets = DeckPresetService
            .LoadOrCreate(_presetPath, currentDeck)
            .Select(DeckPresetService.Clone)
            .ToList();

        RebuildCharacterIndex();
        UserSettings userSettings = UserSettingsService.Load();

        RemoveMissingDeckCharacters();
        RemoveMissingPresetCharacters();
        RefreshPresetList(_presets.FirstOrDefault()?.Id);
        RefreshGroupOptions();
        RefreshGroupFilterOptions();
        RestoreLibraryFilterState(userSettings);
        RefreshAllLists();

        string? lastSelectedCharacterId = userSettings.LastDeckEditorCharacterId;
        string? initialCharacterId = _library.Any(character =>
                string.Equals(character.Id, lastSelectedCharacterId, StringComparison.Ordinal))
            ? lastSelectedCharacterId
            : _deckIds.FirstOrDefault() ?? _library.FirstOrDefault()?.Id;

        _lastSelectedCharacterId = initialCharacterId;
        if (!string.IsNullOrWhiteSpace(initialCharacterId))
        {
            SelectCharacter(initialCharacterId);
        }

        _autoSaveReady = true;
    }

    public IReadOnlyList<CharacterEntry> SavedCharacters { get; private set; }
        = Array.Empty<CharacterEntry>();

    public bool HasSavedToDisk => _hasSavedToDisk;

    private void RefreshPresetList(string? selectedPresetId = null)
    {
        _isRefreshingPresets = true;
        try
        {
            PresetDisplayItem[] items = _presets
                .OrderBy(preset => preset.Name, StringComparer.Ordinal)
                .ThenBy(preset => preset.Id, StringComparer.Ordinal)
                .Select(preset => new PresetDisplayItem(preset))
                .ToArray();

            PresetComboBox.ItemsSource = items;
            PresetDisplayItem? selected = items.FirstOrDefault(item =>
                string.Equals(item.Id, selectedPresetId ?? _selectedPresetId, StringComparison.Ordinal));
            PresetComboBox.SelectedItem = selected;

            if (selected is not null)
            {
                _selectedPresetId = selected.Id;
                PresetNameTextBox.Text = selected.Name;
            }
            else if (items.Length == 0)
            {
                _selectedPresetId = null;
                PresetNameTextBox.Clear();
            }
        }
        finally
        {
            _isRefreshingPresets = false;
        }
    }

    private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingPresets || PresetComboBox.SelectedItem is not PresetDisplayItem item)
        {
            return;
        }

        _selectedPresetId = item.Id;
        PresetNameTextBox.Text = item.Name;
        EditorStatusText.Text = $"'{item.Name}' 프리셋 선택 · 불러오기를 누르면 현재 덱을 교체합니다.";
        EditorStatusText.Foreground = BrushFromHex("#B8EAF5");
    }

    private void LoadPresetButton_Click(object sender, RoutedEventArgs e)
    {
        DeckPreset? preset = FindPreset(_selectedPresetId);
        if (preset is null)
        {
            SetError("불러올 덱 프리셋을 먼저 선택하세요.");
            return;
        }

        var validIds = _library.Select(character => character.Id).ToHashSet(StringComparer.Ordinal);
        string[] validPresetIds = preset.CharacterIds
            .Where(validIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumDeckSize)
            .ToArray();
        List<string> normalizedIds = NormalizeDeckIdsForRestrictions(validPresetIds);
        int skippedCount = validPresetIds.Length - normalizedIds.Count;

        _deckIds.Clear();
        _deckIds.AddRange(normalizedIds);

        RefreshAllLists(_editingCharacterId);
        EditorStatusText.Text = skippedCount > 0
            ? $"'{preset.Name}' 프리셋을 불러왔습니다 · {_deckIds.Count}명 · 모드시프트 형태 {skippedCount}명 제외"
            : $"'{preset.Name}' 프리셋을 불러왔습니다 · {_deckIds.Count}명";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
    }

    private void SavePresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitPendingEditorChanges())
        {
            return;
        }

        if (_deckIds.Count == 0)
        {
            SetError("프리셋으로 저장할 현재 덱이 비어 있습니다.");
            return;
        }

        if (!ValidateCurrentDeckRestrictions(out string restrictionError))
        {
            SetError(restrictionError);
            return;
        }

        string name = PresetNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            SetError("덱 프리셋 이름을 입력하세요.");
            PresetNameTextBox.Focus();
            return;
        }

        DeckPreset? selectedPreset = FindPreset(_selectedPresetId);
        DeckPreset? sameName = _presets.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

        if (selectedPreset is not null &&
            sameName is not null &&
            !string.Equals(selectedPreset.Id, sameName.Id, StringComparison.Ordinal))
        {
            SetError("같은 이름의 다른 덱 프리셋이 이미 있습니다.");
            return;
        }

        DeckPreset targetPreset;
        bool isNew;

        if (selectedPreset is not null)
        {
            targetPreset = selectedPreset;
            isNew = false;
        }
        else if (sameName is not null)
        {
            targetPreset = sameName;
            isNew = false;
        }
        else
        {
            targetPreset = new DeckPreset
            {
                Id = $"preset-{Guid.NewGuid():N}"
            };
            _presets.Add(targetPreset);
            isNew = true;
        }

        targetPreset.Name = name;
        targetPreset.CharacterIds = _deckIds
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumDeckSize)
            .ToList();
        _selectedPresetId = targetPreset.Id;
        RefreshPresetList(targetPreset.Id);

        EditorStatusText.Text = isNew
            ? $"'{name}' 프리셋을 새로 만들었습니다. 자동 저장되었습니다."
            : $"'{name}' 프리셋을 현재 덱으로 갱신했습니다. 자동 저장되었습니다.";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
        AutoSaveCommittedStateSafely();
    }

    private void NewPresetButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedPresetId = null;
        _isRefreshingPresets = true;
        PresetComboBox.SelectedItem = null;
        _isRefreshingPresets = false;
        PresetNameTextBox.Clear();
        PresetNameTextBox.Focus();
        EditorStatusText.Text = "새 프리셋 이름을 입력한 뒤 현재 덱 저장을 누르세요.";
        EditorStatusText.Foreground = BrushFromHex("#AEB8C8");
    }

    private void DeletePresetButton_Click(object sender, RoutedEventArgs e)
    {
        DeckPreset? preset = FindPreset(_selectedPresetId);
        if (preset is null)
        {
            SetError("삭제할 덱 프리셋을 먼저 선택하세요.");
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            $"'{preset.Name}' 프리셋을 삭제할까요?\n현재 덱과 캐릭터 목록은 삭제되지 않습니다.",
            "덱 프리셋 삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _presets.Remove(preset);
        _selectedPresetId = null;
        RefreshPresetList(_presets.FirstOrDefault()?.Id);
        EditorStatusText.Text = $"'{preset.Name}' 프리셋을 삭제했습니다. 자동 저장되었습니다.";
        EditorStatusText.Foreground = BrushFromHex("#FFD08A");
        AutoSaveCommittedStateSafely();
    }

    private void RefreshGroupOptions(string? selectedGroup = null)
    {
        string current = DeckDataService.NormalizeGroupName(
            selectedGroup ?? CharacterGroupComboBox.Text);
        string[] groups = _library
            .Select(character => DeckDataService.NormalizeGroupName(character.GroupName))
            .Concat(_library.SelectMany(character => character.IncludedGroups ?? new List<string>()))
            .Concat(_library.SelectMany(character =>
                character.MiracleLeaderEffect?.TargetGroups ?? new List<string>()))
            .Concat(_library.SelectMany(character =>
                character.DeckGroupLetterEffect?.TargetGroups ?? new List<string>()))
            .Select(DeckDataService.NormalizeGroupName)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.Ordinal)
            .ToArray();

        CharacterGroupComboBox.ItemsSource = groups;
        CharacterGroupComboBox.Text = current;
    }

    private void RefreshAllLists(string? selectedCharacterId = null, int selectedDeckIndex = -1)
    {
        if (_groupFilterOptionsDirty)
        {
            RefreshGroupFilterOptions();
            _groupFilterOptionsDirty = false;
        }

        RefreshLibraryList(selectedCharacterId ?? _editingCharacterId);
        RefreshDeckList(selectedDeckIndex);
        UpdateCountTexts();

        // 캐릭터 추가/수정/삭제와 덱 변경은 대부분 RefreshAllLists를 거칩니다.
        // 여기서 즉시 자동 저장해 두면 마지막에 '저장하고 적용'을 누르지 않았거나
        // 실수로 닫기를 눌러도 이미 반영된 작업이 사라지지 않습니다.
        if (_autoSaveReady)
        {
            AutoSaveCommittedStateSafely();
        }
    }

    private void RefreshGroupFilterOptions()
    {
        string current = GroupFilterComboBox.SelectedItem as string ?? "전체 그룹";
        string[] groups = _library
            .SelectMany(GetEffectiveGroupNamesCached)
            .Select(DeckDataService.NormalizeGroupName)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.Ordinal)
            .ToArray();
        string[] items = new[] { "전체 그룹" }.Concat(groups).ToArray();

        _isRefreshingGroupFilter = true;
        try
        {
            GroupFilterComboBox.ItemsSource = items;
            GroupFilterComboBox.SelectedItem = items.Contains(current, StringComparer.OrdinalIgnoreCase)
                ? items.First(item => string.Equals(item, current, StringComparison.OrdinalIgnoreCase))
                : "전체 그룹";
        }
        finally
        {
            _isRefreshingGroupFilter = false;
            _groupFilterOptionsDirty = false;
        }
    }

    private void RestoreLibraryFilterState(UserSettings settings)
    {
        LibrarySearchTextBox.Text = settings.LastDeckEditorSearchText ?? string.Empty;
        SelectComboBoxValue(CategoryFilterComboBox, settings.LastDeckEditorCategoryFilter, "전체 등급");
        SelectComboBoxValue(AttributeFilterComboBox, settings.LastDeckEditorAttributeFilter, "전체 속성");
        SelectComboBoxValue(SpeciesFilterComboBox, settings.LastDeckEditorSpeciesFilter, "전체 종족");
        SelectComboBoxValue(GroupFilterComboBox, settings.LastDeckEditorGroupFilter, "전체 그룹");
        SelectComboBoxValue(StatusFilterComboBox, settings.LastDeckEditorStatusFilter, "전체 상태");
        SelectComboBoxValue(SortComboBox, settings.LastDeckEditorSortMode, "기본 정렬");
        FavoriteOnlyCheckBox.IsChecked = settings.LastDeckEditorFavoritesOnly;
        BelovedOnlyCheckBox.IsChecked = settings.LastDeckEditorBelovedOnly;
        UpdateLibrarySearchHint();
    }

    private void SelectComboBoxValue(ComboBox comboBox, string? savedValue, string fallback)
    {
        string desired = string.IsNullOrWhiteSpace(savedValue) ? fallback : savedValue.Trim();
        object? selected = comboBox.Items
            .Cast<object>()
            .FirstOrDefault(item => string.Equals(item?.ToString(), desired, StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedItem = selected ?? comboBox.Items
            .Cast<object>()
            .FirstOrDefault(item => string.Equals(item?.ToString(), fallback, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshLibraryList(string? selectedCharacterId = null)
    {
        _isRefreshingLists = true;
        try
        {
            SearchTokenQuery[] searchTokens = BuildSearchTokenQueries(LibrarySearchTextBox.Text);
            string selectedCategory = CategoryFilterComboBox.SelectedItem as string ?? "전체 등급";
            string selectedAttribute = AttributeFilterComboBox.SelectedItem as string ?? "전체 속성";
            string selectedSpecies = SpeciesFilterComboBox.SelectedItem as string ?? "전체 종족";
            string selectedGroup = GroupFilterComboBox.SelectedItem as string ?? "전체 그룹";
            string selectedStatus = StatusFilterComboBox.SelectedItem as string ?? "전체 상태";
            string selectedSort = SortComboBox.SelectedItem as string ?? "기본 정렬";
            bool favoritesOnly = FavoriteOnlyCheckBox.IsChecked == true;
            bool belovedOnly = BelovedOnlyCheckBox.IsChecked == true;
            IEnumerable<CharacterEntry> filtered = _library;

            if (searchTokens.Length > 0)
            {
                filtered = filtered.Where(character => searchTokens.All(token =>
                    CharacterMatchesSearchToken(character, token)));
            }

            if (!string.Equals(selectedCategory, "전체 등급", StringComparison.Ordinal))
            {
                filtered = filtered.Where(character =>
                    string.Equals(
                        CharacterCategories.Normalize(character.Category),
                        selectedCategory,
                        StringComparison.Ordinal));
            }

            if (!string.Equals(selectedAttribute, "전체 속성", StringComparison.Ordinal))
            {
                filtered = string.Equals(selectedAttribute, "미입력", StringComparison.Ordinal)
                    ? filtered.Where(character => GetAllCharacterAttributes(character).Count == 0)
                    : filtered.Where(character => GetAllCharacterAttributes(character)
                        .Contains(selectedAttribute, StringComparer.Ordinal));
            }

            if (!string.Equals(selectedSpecies, "전체 종족", StringComparison.Ordinal))
            {
                filtered = string.Equals(selectedSpecies, "미입력", StringComparison.Ordinal)
                    ? filtered.Where(character => GetAllCharacterSpecies(character).Count == 0)
                    : filtered.Where(character => GetAllCharacterSpecies(character)
                        .Contains(selectedSpecies, StringComparer.Ordinal));
            }

            if (!string.Equals(selectedGroup, "전체 그룹", StringComparison.Ordinal))
            {
                filtered = filtered.Where(character =>
                    GetEffectiveGroupNamesCached(character).Any(group =>
                        string.Equals(
                            DeckDataService.NormalizeGroupName(group),
                            DeckDataService.NormalizeGroupName(selectedGroup),
                            StringComparison.OrdinalIgnoreCase)));
            }

            filtered = selectedStatus switch
            {
                "현재 덱" => filtered.Where(character =>
                    _deckIds.Contains(character.Id, StringComparer.Ordinal)),
                "현재 덱 제외" => filtered.Where(character =>
                    !_deckIds.Contains(character.Id, StringComparer.Ordinal)),
                "이미지 없음" => filtered.Where(character =>
                    string.IsNullOrWhiteSpace(character.ImageFileName) ||
                    (character.AlternateForms ?? new List<CharacterForm>())
                        .Any(form => string.IsNullOrWhiteSpace(form.ImageFileName))),
                "그룹 없음" => filtered.Where(character =>
                    string.IsNullOrWhiteSpace(character.GroupName)),
                "동일명 모드시프트" => filtered.Where(character =>
                    character.HasAlternateForms),
                "이름 다른 모드시프트" => filtered.Where(character =>
                    !string.IsNullOrWhiteSpace(character.DeckRestrictionGroupId)),
                "조건 문자 보유" => filtered.Where(character =>
                    character.LetterStates.Count > 0),
                "미라클 효과 보유" => filtered.Where(character =>
                    DeckDataService.NormalizeMiracleLeaderEffect(character.MiracleLeaderEffect).IsConfigured),
                "덱 그룹 조건 문자 보유" => filtered.Where(character =>
                    DeckDataService.NormalizeDeckGroupLetterEffect(character.DeckGroupLetterEffect).IsConfigured),
                "미완성 정보" => filtered.Where(character =>
                    string.IsNullOrWhiteSpace(character.ImageFileName) ||
                    (character.AlternateForms ?? new List<CharacterForm>())
                        .Any(form => string.IsNullOrWhiteSpace(form.ImageFileName)) ||
                    string.IsNullOrWhiteSpace(character.GroupName) ||
                    string.IsNullOrWhiteSpace(character.Attribute) ||
                    string.IsNullOrWhiteSpace(character.Species)),
                _ => filtered
            };

            if (favoritesOnly)
            {
                filtered = filtered.Where(character => character.IsFavorite);
            }

            if (belovedOnly)
            {
                filtered = filtered.Where(character => character.IsBeloved);
            }

            IOrderedEnumerable<CharacterEntry> ordered = selectedSort switch
            {
                "이름순" => filtered
                    .OrderBy(character => character.Name, StringComparer.Ordinal)
                    .ThenBy(character => CharacterCategories.GetSortOrder(character.Category)),
                "속성순" => filtered
                    .OrderBy(character => GetAttributeSortOrder(character.Attribute))
                    .ThenBy(character => CharacterCategories.GetSortOrder(character.Category))
                    .ThenBy(character => character.Name, StringComparer.Ordinal),
                "종족순" => filtered
                    .OrderBy(character => GetSpeciesSortOrder(character.Species))
                    .ThenBy(character => CharacterCategories.GetSortOrder(character.Category))
                    .ThenBy(character => character.Name, StringComparer.Ordinal),
                "현재 덱 우선" => filtered
                    .OrderByDescending(character => _deckIds.Contains(character.Id, StringComparer.Ordinal))
                    .ThenByDescending(character => character.IsFavorite)
                    .ThenByDescending(character => character.IsBeloved)
                    .ThenBy(character => CharacterCategories.GetSortOrder(character.Category))
                    .ThenBy(character => character.Name, StringComparer.Ordinal),
                "이미지 없음 우선" => filtered
                    .OrderByDescending(character =>
                        string.IsNullOrWhiteSpace(character.ImageFileName) ||
                        (character.AlternateForms ?? new List<CharacterForm>())
                            .Any(form => string.IsNullOrWhiteSpace(form.ImageFileName)))
                    .ThenByDescending(character => character.IsFavorite)
                    .ThenByDescending(character => character.IsBeloved)
                    .ThenBy(character => CharacterCategories.GetSortOrder(character.Category))
                    .ThenBy(character => character.Name, StringComparer.Ordinal),
                "그룹 없음 우선" => filtered
                    .OrderByDescending(character => string.IsNullOrWhiteSpace(character.GroupName))
                    .ThenByDescending(character => character.IsFavorite)
                    .ThenByDescending(character => character.IsBeloved)
                    .ThenBy(character => CharacterCategories.GetSortOrder(character.Category))
                    .ThenBy(character => character.Name, StringComparer.Ordinal),
                _ => filtered
                    .OrderByDescending(character => character.IsFavorite)
                    .ThenByDescending(character => character.IsBeloved)
                    .ThenBy(character => CharacterCategories.GetSortOrder(character.Category))
                    .ThenBy(character => character.Name, StringComparer.Ordinal)
            };

            Dictionary<string, int> deckIndexById = _deckIds
                .Select((id, index) => new { id, index })
                .ToDictionary(item => item.id, item => item.index, StringComparer.Ordinal);

            CharacterEntry[] orderedCharacters = ordered
                .ThenBy(character => character.Id, StringComparer.Ordinal)
                .ToArray();

            _lastFilteredLibraryCount = orderedCharacters.Length;
            _libraryPageCount = Math.Max(1, (int)Math.Ceiling(orderedCharacters.Length / (double)LibraryPageSize));

            if (!string.IsNullOrWhiteSpace(selectedCharacterId))
            {
                int selectedAbsoluteIndex = Array.FindIndex(
                    orderedCharacters,
                    character => string.Equals(character.Id, selectedCharacterId, StringComparison.Ordinal));
                if (selectedAbsoluteIndex >= 0)
                {
                    _libraryPageIndex = selectedAbsoluteIndex / LibraryPageSize;
                }
            }

            _libraryPageIndex = Math.Clamp(_libraryPageIndex, 0, _libraryPageCount - 1);
            CharacterEntry[] pageCharacters = orderedCharacters
                .Skip(_libraryPageIndex * LibraryPageSize)
                .Take(LibraryPageSize)
                .ToArray();

            CharacterDisplayItem[] items = pageCharacters
                .Select(character => new CharacterDisplayItem(
                    character,
                    () => GetCharacterThumbnail(character),
                    () => GetCharacterListSummary(character),
                    deckIndexById.TryGetValue(character.Id, out int deckIndex) ? deckIndex : -1))
                .ToArray();

            CharacterLibraryListBox.ItemsSource = items;
            LibraryPageText.Text = $"{_libraryPageIndex + 1} / {_libraryPageCount}";
            LibraryPrevPageButton.IsEnabled = _libraryPageIndex > 0;
            LibraryNextPageButton.IsEnabled = _libraryPageIndex + 1 < _libraryPageCount;

            if (!string.IsNullOrWhiteSpace(selectedCharacterId))
            {
                CharacterDisplayItem? selected = items.FirstOrDefault(item =>
                    string.Equals(item.Id, selectedCharacterId, StringComparison.Ordinal));
                CharacterLibraryListBox.SelectedItem = selected;
                if (selected is not null)
                {
                    CharacterLibraryListBox.ScrollIntoView(selected);
                }
            }
        }
        finally
        {
            _isRefreshingLists = false;
        }
    }

    private static SearchTokenQuery[] BuildSearchTokenQueries(string? text)
    {
        string[] tokens = (text ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return Array.Empty<SearchTokenQuery>();
        }

        return tokens
            .Select(token =>
            {
                string normalized = KanaUtility.ToSearchKey(token);
                string converted = KanaUtility.ToSearchKey(
                    KanaUtility.ConvertHangulToKana(normalized));
                string[] allCells = KanaUtility.SplitIntoCells(converted).ToArray();
                string[] cells = allCells.Length > 1 && allCells.All(KanaUtility.IsJapaneseCell)
                    ? allCells
                    : Array.Empty<string>();
                return new SearchTokenQuery(normalized, converted, cells);
            })
            .ToArray();
    }

    private bool CharacterMatchesSearchToken(CharacterEntry character, SearchTokenQuery token)
    {
        string searchable = GetSearchCandidates(character);

        if (searchable.Contains(token.Normalized, StringComparison.OrdinalIgnoreCase) ||
            (!string.Equals(token.Converted, token.Normalized, StringComparison.Ordinal) &&
             searchable.Contains(token.Converted, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 한글로 여러 글자를 붙여 치면(예: '코키'→こ+き) 문자들이 Letters 배열처럼
        // 낱개로 흩어져 있어도 모두 포함하면 일치로 봅니다.
        return token.Cells.Length > 1 &&
               token.Cells.All(cell =>
                   searchable.Contains(cell, StringComparison.OrdinalIgnoreCase));
    }

    private string GetSearchCandidates(CharacterEntry character)
    {
        if (_searchCandidateCache.TryGetValue(character.Id, out string? cached))
        {
            return cached;
        }

        IEnumerable<string> searchableValues = new[]
            {
                character.Name,
                string.Join(" ", DeckDataService.NormalizeSearchAliases(character.SearchAliases)),
                string.Join(" ", CharacterSearchAliasUtility.BuildAutomaticAliases(character.Name)),
                CharacterCategories.Normalize(character.Category),
                NormalizeAttribute(character.Attribute),
                string.Join(" ", DeckDataService.NormalizeAttributes(character.SubAttributes, character.Attribute)),
                NormalizeSpecies(character.Species),
                character.GroupName ?? string.Empty,
                string.Join(" ", character.IncludedGroups ?? new List<string>()),
                string.Concat(character.Letters),
                string.Join(" ", character.Letters),
                string.Join(" ", character.MiracleLeaderEffect?.TargetGroups ?? new List<string>()),
                string.Join(" ", character.MiracleLeaderEffect?.GrantedLetters ?? new List<string>()),
                string.Join(" ", character.DeckGroupLetterEffect?.TargetGroups ?? new List<string>()),
                string.Join(" ", character.DeckGroupLetterEffect?.GrantedLetters ?? new List<string>()),
                (character.DeckGroupLetterEffect?.MinimumCount ?? 0).ToString(CultureInfo.InvariantCulture)
            }
            .Concat((character.LetterStates ?? new List<CharacterLetterState>()).SelectMany(state => new[]
            {
                state.Name,
                CharacterLetterStateKinds.Normalize(state.Kind),
                string.Concat(state.Letters),
                state.Note ?? string.Empty
            }))
            .Concat((character.AlternateForms ?? new List<CharacterForm>()).SelectMany(form => new[]
            {
                form.Name,
                string.Concat(form.Letters),
                string.Join(" ", form.Letters),
                DeckDataService.NormalizeAttribute(form.Attribute),
                string.Join(" ", DeckDataService.NormalizeAttributes(form.SubAttributes, form.Attribute)),
                DeckDataService.NormalizeSpecies(form.Species),
                form.Note ?? string.Empty
            }))
            .Concat(GetRelatedForms(character).Select(related => related.Name));

        // 후보 배열을 매 검색마다 순회하지 않도록 검색 전용 문자열 하나로 캐시합니다.
        // 제어문자 구분자를 사용해 서로 다른 필드 경계에서 우연히 문자열이 이어지는 것도 막습니다.
        string searchable = string.Join(
            "\u001F",
            searchableValues
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(KanaUtility.ToSearchKey)
                .Distinct(StringComparer.OrdinalIgnoreCase));
        _searchCandidateCache[character.Id] = searchable;
        return searchable;
    }

    private string[] GetEffectiveGroupNamesCached(CharacterEntry character)
    {
        if (_effectiveGroupCache.TryGetValue(character.Id, out string[]? cached))
        {
            return cached;
        }

        string[] groups = DeckDataService.GetEffectiveGroupNames(character)
            .Select(DeckDataService.NormalizeGroupName)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _effectiveGroupCache[character.Id] = groups;
        return groups;
    }

    private void RefreshDeckList(int selectedIndex = -1)
    {
        _isRefreshingLists = true;
        try
        {
            var items = new List<DeckDisplayItem>();

            for (int index = 0; index < _deckIds.Count; index++)
            {
                CharacterEntry? character = FindCharacter(_deckIds[index]);
                if (character is null)
                {
                    continue;
                }

                items.Add(new DeckDisplayItem(
                    index,
                    character,
                    GetCharacterThumbnail(character),
                    GetCharacterListSummary(character)));
            }

            DeckListBox.ItemsSource = items;

            if (items.Count > 0 && selectedIndex >= 0)
            {
                int safeIndex = Math.Clamp(selectedIndex, 0, items.Count - 1);
                DeckListBox.SelectedIndex = safeIndex;
                DeckListBox.ScrollIntoView(items[safeIndex]);
            }
        }
        finally
        {
            _isRefreshingLists = false;
        }
    }

    private void UpdateCountTexts()
    {
        LibraryCountText.Text = _lastFilteredLibraryCount == _library.Count
            ? $"{_library.Count:N0}명"
            : $"검색 {_lastFilteredLibraryCount:N0} / 전체 {_library.Count:N0}명";
        DeckCountText.Text = $"{_deckIds.Count} / {MaximumDeckSize}명";
    }

    private void LibrarySearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateLibrarySearchHint();

        if (!IsLoaded)
        {
            return;
        }

        _libraryPageIndex = 0;
        _librarySearchTimer.Stop();
        _librarySearchTimer.Start();
    }

    private void LibrarySearchTimer_Tick(object? sender, EventArgs e)
    {
        _librarySearchTimer.Stop();
        RefreshLibraryList();
        UpdateCountTexts();
    }

    private void UpdateLibrarySearchHint()
    {
        string text = LibrarySearchTextBox.Text;
        string converted = KanaUtility.ConvertHangulToKana(text);
        LibrarySearchHintText.Text = converted != text
            ? $"가나 변환: {text} → {converted}"
            : "예: 仮面ライダー し · 속성·종족도 함께 검색할 수 있습니다";
    }

    private void LibrarySelectionFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingGroupFilter)
        {
            return;
        }

        RefreshLibraryFilters();

    }

    private void LibraryCheckFilter_Changed(object sender, RoutedEventArgs e)
        => RefreshLibraryFilters();

    private void RefreshLibraryFilters()
    {
        if (!IsLoaded)
        {
            return;
        }

        _librarySearchTimer.Stop();
        _libraryPageIndex = 0;
        RefreshLibraryList();
        UpdateCountTexts();
    }

    private void ClearLibrarySearchButton_Click(object sender, RoutedEventArgs e)
    {
        _librarySearchTimer.Stop();
        LibrarySearchTextBox.Clear();
        CategoryFilterComboBox.SelectedIndex = 0;
        AttributeFilterComboBox.SelectedIndex = 0;
        SpeciesFilterComboBox.SelectedIndex = 0;
        GroupFilterComboBox.SelectedIndex = 0;
        StatusFilterComboBox.SelectedIndex = 0;
        SortComboBox.SelectedIndex = 0;
        FavoriteOnlyCheckBox.IsChecked = false;
        BelovedOnlyCheckBox.IsChecked = false;
        _libraryPageIndex = 0;
        RefreshLibraryList();
        UpdateCountTexts();
        LibrarySearchTextBox.Focus();
    }

    private void LibraryPrevPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryPageIndex <= 0)
        {
            return;
        }

        _libraryPageIndex--;
        RefreshLibraryList();
        UpdateCountTexts();
    }

    private void LibraryNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryPageIndex + 1 >= _libraryPageCount)
        {
            return;
        }

        _libraryPageIndex++;
        RefreshLibraryList();
        UpdateCountTexts();
    }

    private void CharacterLibraryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingLists || CharacterLibraryListBox.SelectedItem is not CharacterDisplayItem item)
        {
            return;
        }

        BeginEditing(item.Id);
    }

    private void DeckListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingLists || DeckListBox.SelectedItem is not DeckDisplayItem item)
        {
            return;
        }

        BeginEditing(item.Id, selectLibraryItem: true);
    }

    private void CharacterLibraryListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CharacterLibraryListBox.SelectedItem is CharacterDisplayItem item)
        {
            AddCharacterToDeck(item.Id);
        }
    }

    private void DeckListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => RemoveSelectedDeckCharacter();

    private void CharacterLibraryListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelLibraryModeShiftHold();
        _modeShiftHoldTriggered = false;

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (source is null || FindVisualAncestor<Button>(source) is not null)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(CharacterLibraryListBox, source) is not ListBoxItem container ||
            container.DataContext is not CharacterDisplayItem item)
        {
            return;
        }

        CharacterEntry? character = FindCharacter(item.Id);
        if (character is null || !character.HasAlternateForms)
        {
            return;
        }

        _pendingLibraryModeShiftCharacterId = character.Id;
        _libraryModeShiftHoldTimer.Start();
    }

    private void CharacterLibraryListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool triggered = _modeShiftHoldTriggered;
        CancelLibraryModeShiftHold();
        _modeShiftHoldTriggered = false;
        if (triggered)
        {
            e.Handled = true;
        }
    }

    private void DeckListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelDeckModeShiftHold();
        _modeShiftHoldTriggered = false;

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (source is null || FindVisualAncestor<Button>(source) is not null)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(DeckListBox, source) is not ListBoxItem container ||
            container.DataContext is not DeckDisplayItem item)
        {
            return;
        }

        CharacterEntry? character = FindCharacter(item.Id);
        bool canSwitch = character is not null &&
                         (!string.IsNullOrWhiteSpace(character.DeckRestrictionGroupId) || character.HasAlternateForms);
        if (!canSwitch)
        {
            return;
        }

        _pendingDeckModeShiftIndex = item.Index;
        _deckModeShiftHoldTimer.Start();
    }

    private void DeckListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        bool triggered = _modeShiftHoldTriggered;
        CancelDeckModeShiftHold();
        _modeShiftHoldTriggered = false;
        if (triggered)
        {
            e.Handled = true;
        }
    }

    private void LibraryModeShiftHoldTimer_Tick(object? sender, EventArgs e)
    {
        _libraryModeShiftHoldTimer.Stop();
        string? characterId = _pendingLibraryModeShiftCharacterId;
        _pendingLibraryModeShiftCharacterId = null;
        if (string.IsNullOrWhiteSpace(characterId) || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        CharacterEntry? character = FindCharacter(characterId);
        if (character is null || !character.HasAlternateForms)
        {
            return;
        }

        _modeShiftHoldTriggered = true;
        CycleSameNameModeShift(character, GetSelectedDeckIndexForCharacter(character.Id));
    }

    private void DeckModeShiftHoldTimer_Tick(object? sender, EventArgs e)
    {
        _deckModeShiftHoldTimer.Stop();
        int deckIndex = _pendingDeckModeShiftIndex;
        _pendingDeckModeShiftIndex = -1;
        if (deckIndex < 0 || deckIndex >= _deckIds.Count || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        CharacterEntry? current = FindCharacter(_deckIds[deckIndex]);
        if (current is null)
        {
            return;
        }

        _modeShiftHoldTriggered = true;

        // 덱에서는 이름이 다른 연결형 모드시프트를 우선합니다.
        // 동일명 형태만 있는 캐릭터는 같은 자리에서 내부 형태를 순환합니다.
        if (TryCycleConnectedModeShift(deckIndex, current))
        {
            return;
        }

        if (current.HasAlternateForms)
        {
            CycleSameNameModeShift(current, deckIndex);
        }
    }

    private void CycleSameNameModeShift(CharacterEntry character, int selectedDeckIndex)
    {
        var formIds = new List<string> { CharacterEntry.BaseFormId };
        formIds.AddRange((character.AlternateForms ?? new List<CharacterForm>())
            .Where(form => !string.IsNullOrWhiteSpace(form.Id))
            .Select(form => form.Id));
        if (formIds.Count <= 1)
        {
            return;
        }

        string currentId = string.IsNullOrWhiteSpace(character.ActiveFormId)
            ? CharacterEntry.BaseFormId
            : character.ActiveFormId;
        int currentIndex = formIds.FindIndex(id => string.Equals(id, currentId, StringComparison.Ordinal));
        int nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % formIds.Count;
        character.ActiveFormId = formIds[nextIndex];
        // 동일명 형태 자체는 JSON의 ActiveFormId로 저장하지 않지만,
        // 자동 저장 시 SavedCharacters 스냅샷에 현재 형태를 남겨 메인 손패와 동기화합니다.
        AutoSaveCommittedStateSafely();

        InvalidateCharacterThumbnail(character.Id);
        RefreshLibraryList(character.Id);
        RefreshDeckList(selectedDeckIndex);
        UpdateCountTexts();

        string activeName = character.GetActiveFormName();
        EditorStatusText.Text = $"'{character.Name}' 동일명 모드시프트 → {activeName}";
        EditorStatusText.Foreground = BrushFromHex("#D9C2FF");
    }

    private bool TryCycleConnectedModeShift(int deckIndex, CharacterEntry current)
    {
        if (!TryCommitPendingEditorChanges())
        {
            return true;
        }

        string group = NormalizeRestrictionGroup(current.DeckRestrictionGroupId);
        if (group.Length == 0)
        {
            return false;
        }

        CharacterEntry[] members = _library
            .Where(character => string.Equals(
                NormalizeRestrictionGroup(character.DeckRestrictionGroupId),
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
            bool alreadyUsedElsewhere = _deckIds
                .Where((_, index) => index != deckIndex)
                .Contains(candidate.Id, StringComparer.Ordinal);
            if (!alreadyUsedElsewhere)
            {
                target = candidate;
                break;
            }
        }

        if (target is null)
        {
            EditorStatusText.Text = "전환 가능한 연결형 모드시프트가 현재 덱의 다른 칸에 이미 편성되어 있습니다.";
            EditorStatusText.Foreground = BrushFromHex("#FFD08A");
            return true;
        }

        _deckIds[deckIndex] = target.Id;
        AutoSaveCommittedStateSafely();
        RefreshAllLists(target.Id, deckIndex);
        BeginEditing(target.Id, selectLibraryItem: true);
        EditorStatusText.Text = $"덱 {deckIndex + 1}번 연결형 모드시프트 · {current.Name} → {target.Name}";
        EditorStatusText.Foreground = BrushFromHex("#D9C2FF");
        return true;
    }

    private void CancelLibraryModeShiftHold()
    {
        _libraryModeShiftHoldTimer.Stop();
        _pendingLibraryModeShiftCharacterId = null;
    }

    private void CancelDeckModeShiftHold()
    {
        _deckModeShiftHoldTimer.Stop();
        _pendingDeckModeShiftIndex = -1;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T target)
            {
                return target;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void BeginEditing(string characterId, bool selectLibraryItem = false)
    {
        CharacterEntry? character = FindCharacter(characterId);
        if (character is null)
        {
            return;
        }

        _editingCharacterId = character.Id;
        _lastSelectedCharacterId = character.Id;
        _isRelatedFormEditorOpen = false;
        RelatedFormEditorPanel.Visibility = Visibility.Collapsed;
        ToggleRelatedFormEditorButton.Content = "연결 설정";
        ToggleRelatedFormEditorButton.IsEnabled = true;
        CharacterNameTextBox.Text = character.Name;
        CharacterSearchAliasesTextBox.Text = string.Join(" · ", DeckDataService.NormalizeSearchAliases(character.SearchAliases));
        CharacterCategoryComboBox.SelectedItem = CharacterCategories.Normalize(character.Category);
        CharacterAttributeComboBox.SelectedItem = MetadataComboValue(NormalizeAttribute(character.Attribute));
        CharacterSubAttributesTextBox.Text = string.Join(" / ", DeckDataService.NormalizeAttributes(character.SubAttributes, character.Attribute));
        CharacterSpeciesComboBox.SelectedItem = MetadataComboValue(NormalizeSpecies(character.Species));
        CharacterFavoriteCheckBox.IsChecked = character.IsFavorite;
        CharacterBelovedCheckBox.IsChecked = character.IsBeloved;
        CharacterLettersTextBox.Text = string.Join(" ", character.Letters);
        RefreshGroupOptions(character.GroupName);
        CharacterIncludedGroupsTextBox.Text = string.Join(", ", character.IncludedGroups);
        MiracleLeaderEffect miracleEffect = DeckDataService.NormalizeMiracleLeaderEffect(
            character.MiracleLeaderEffect);
        MiracleLeaderEffectCheckBox.IsChecked = miracleEffect.IsConfigured;
        MiracleTargetGroupsTextBox.Text = string.Join(", ", miracleEffect.TargetGroups);
        MiracleGrantedLettersTextBox.Text = string.Join(" ", miracleEffect.GrantedLetters);
        MiracleEffectNoteTextBox.Text = miracleEffect.Note;
        DeckGroupLetterEffect deckGroupEffect = DeckDataService.NormalizeDeckGroupLetterEffect(
            character.DeckGroupLetterEffect);
        DeckGroupLetterEffectCheckBox.IsChecked = deckGroupEffect.IsConfigured;
        DeckGroupConditionGroupsTextBox.Text = string.Join(", ", deckGroupEffect.TargetGroups);
        DeckGroupConditionMinimumCountTextBox.Text = deckGroupEffect.MinimumCount.ToString(CultureInfo.InvariantCulture);
        DeckGroupConditionGrantedLettersTextBox.Text = string.Join(" ", deckGroupEffect.GrantedLetters);
        DeckGroupConditionNoteTextBox.Text = deckGroupEffect.Note;
        _editingImageFileName = Path.GetFileName(character.ImageFileName ?? string.Empty);
        _pendingImageSourcePath = null;
        _removeImageRequested = false;
        UpdateCharacterImagePreview();
        RelatedFormComboBox.ItemsSource = Array.Empty<RelatedFormDisplayItem>();
        RelatedFormComboBox.SelectedIndex = -1;
        LinkRelatedFormButton.IsEnabled = false;
        UnlinkRelatedFormButton.IsEnabled =
            !string.IsNullOrWhiteSpace(character.DeckRestrictionGroupId);
        UpdateRelatedFormsSummary(character);
        _editingLetterStates = character.LetterStates
            .Select(CharacterLibraryService.CloneState)
            .ToList();
        _editingForms = (character.AlternateForms ?? new List<CharacterForm>())
            .Select(CharacterLibraryService.CloneForm)
            .ToList();
        UpdateCharacterStateSummary();
        UpdateCharacterFormSummary();
        AddCharacterButton.IsEnabled = true;
        UpdateCharacterButton.IsEnabled = true;

        EditorStatusText.Text = $"'{character.Name}' 편집 중 · 수정 버튼을 누르면 자동 저장되고, 창을 닫을 때도 마지막 편집을 저장합니다.";
        EditorStatusText.Foreground = BrushFromHex("#B8EAF5");

        if (selectLibraryItem)
        {
            CharacterDisplayItem? visibleItem = CharacterLibraryListBox.Items
                .OfType<CharacterDisplayItem>()
                .FirstOrDefault(item => string.Equals(item.Id, characterId, StringComparison.Ordinal));
            if (visibleItem is not null)
            {
                _isRefreshingLists = true;
                CharacterLibraryListBox.SelectedItem = visibleItem;
                _isRefreshingLists = false;
                CharacterLibraryListBox.ScrollIntoView(visibleItem);
            }
        }
    }

    private void NewCharacterButton_Click(object sender, RoutedEventArgs e)
    {
        CharacterDetailExpander.IsExpanded = true;
        ClearEditorForNewCharacter();

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                CharacterDetailExpander.BringIntoView();
                CharacterNameTextBox.Focus();
                Keyboard.Focus(CharacterNameTextBox);
            }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BulkEditCharactersButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitPendingEditorChanges())
        {
            return;
        }

        var window = new CharacterBulkEditorWindow(_library, _dataDirectory)
        {
            Owner = this
        };

        if (window.ShowDialog() != true || window.ChangedCharacterIds.Count == 0)
        {
            return;
        }

        string[] changedIds = window.ChangedCharacterIds.ToArray();
        DeckDataService.SynchronizeSharedGroupInclusions(_library);
        foreach (string characterId in changedIds)
        {
            InvalidateCharacterThumbnail(characterId);
        }

        MarkLibraryIndexesDirty();
        RefreshGroupOptions();
        RefreshGroupFilterOptions();
        string? currentEditingId = _editingCharacterId;
        RefreshAllLists(currentEditingId);
        if (!string.IsNullOrWhiteSpace(currentEditingId))
        {
            // 일괄 수정한 캐릭터가 상세 편집창에도 열려 있었다면 오래된 입력값으로
            // 다시 덮어쓰지 않도록 최신 데이터로 편집창을 다시 채웁니다.
            BeginEditing(currentEditingId);
        }

        EditorStatusText.Text = $"캐릭터 {changedIds.Length:N0}명을 일괄 수정했습니다. 연속 작업을 묶어 자동 저장합니다.";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
    }

    private void ImportCharacterFromWebButton_Click(object sender, RoutedEventArgs e)
    {
        string[] knownGroups = _library
            .SelectMany(character => new[] { character.GroupName }
                .Concat(character.IncludedGroups ?? new List<string>()))
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, string[]> knownGroupRelations = _library
            .Where(character => !string.IsNullOrWhiteSpace(character.GroupName))
            .GroupBy(character => character.GroupName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(character => character.IncludedGroups ?? new List<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var window = new CharacterWebImportWindow(knownGroups, knownGroupRelations, _library)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        if (window.ImportedPreviews.Count > 0)
        {
            ImportCharacterBatch(window.ImportedPreviews);
            return;
        }

        if (window.ImportedPreview is not CharacterImportPreview preview)
        {
            return;
        }

        CharacterDetailExpander.IsExpanded = true;
        ClearEditorForNewCharacter();
        CharacterNameTextBox.Text = preview.Name;
        CharacterCategoryComboBox.SelectedItem = CharacterCategories.Normalize(preview.Category);
        CharacterAttributeComboBox.SelectedItem = MetadataComboValue(NormalizeAttribute(preview.Attribute));
        CharacterSubAttributesTextBox.Text = string.Join(" / ", preview.SubAttributes);
        CharacterSpeciesComboBox.SelectedItem = MetadataComboValue(NormalizeSpecies(preview.Species));
        CharacterFavoriteCheckBox.IsChecked = preview.IsFavorite;
        CharacterBelovedCheckBox.IsChecked = false;
        CharacterLettersTextBox.Text = string.Join(" ", preview.Letters);
        RefreshGroupOptions(preview.GroupName);
        CharacterGroupComboBox.Text = preview.GroupName;
        CharacterIncludedGroupsTextBox.Text = string.Join(" · ", preview.IncludedGroups);
        _pendingImageSourcePath = string.IsNullOrWhiteSpace(preview.DownloadedImagePath)
            ? null
            : preview.DownloadedImagePath;
        _removeImageRequested = false;
        UpdateCharacterImagePreview();

        string sourceNote = preview.MatchedDatabaseUrl.Length > 0
            ? $"{preview.SourceSite} + 코토다망DB"
            : preview.SourceSite;
        EditorStatusText.Text = $"'{preview.Name}' 정보를 {sourceNote}에서 가져왔습니다. 내용을 확인한 뒤 '새 캐릭터 추가'를 누르세요.";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                CharacterDetailExpander.BringIntoView();
                CharacterNameTextBox.Focus();
                CharacterNameTextBox.SelectAll();
                Keyboard.Focus(CharacterNameTextBox);
            }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ImportCharacterBatch(IReadOnlyList<CharacterImportPreview> previews)
    {
        int addedCount = 0;
        int duplicateCount = 0;
        int invalidCount = 0;
        int imageFailureCount = 0;
        string? lastAddedCharacterId = null;

        try
        {
            foreach (CharacterImportPreview preview in previews)
            {
                string name = (preview.Name ?? string.Empty).Trim();
                List<string> letters = (preview.Letters ?? new List<string>())
                    .Select(KanaUtility.NormalizeCell)
                    .Where(letter => letter.Length > 0 && KanaUtility.IsJapaneseCell(letter))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                if (name.Length == 0 || letters.Count == 0)
                {
                    invalidCount++;
                    continue;
                }

                bool exactDuplicate = _library.Any(character =>
                    string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    new HashSet<string>(
                        (character.Letters ?? new List<string>())
                            .Select(KanaUtility.NormalizeCell)
                            .Where(letter => letter.Length > 0),
                        StringComparer.Ordinal)
                    .SetEquals(letters));
                if (exactDuplicate)
                {
                    duplicateCount++;
                    continue;
                }

                string groupName = DeckDataService.NormalizeGroupName(preview.GroupName);
                List<string> includedGroups = DeckDataService.NormalizeGroupNames(preview.IncludedGroups)
                    .Where(group => !string.Equals(group, groupName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var character = new CharacterEntry
                {
                    Id = $"character-{Guid.NewGuid():N}",
                    Name = name,
                    Category = CharacterCategories.Normalize(preview.Category),
                    Attribute = NormalizeAttribute(preview.Attribute),
                    SubAttributes = DeckDataService.NormalizeAttributes(preview.SubAttributes, preview.Attribute),
                    Species = NormalizeSpecies(preview.Species),
                    GroupName = groupName,
                    IncludedGroups = includedGroups,
                    IsFavorite = preview.IsFavorite,
                    Letters = letters,
                    DeckRestrictionGroupId = string.Empty,
                    MiracleLeaderEffect = new MiracleLeaderEffect(),
                    DeckGroupLetterEffect = new DeckGroupLetterEffect(),
                    LetterStates = new List<CharacterLetterState>(),
                    AlternateForms = new List<CharacterForm>()
                };

                if (!string.IsNullOrWhiteSpace(preview.DownloadedImagePath) &&
                    File.Exists(preview.DownloadedImagePath))
                {
                    try
                    {
                        character.ImageFileName = CharacterImageService.SaveImageCopy(
                            preview.DownloadedImagePath,
                            _dataDirectory,
                            character.Id);
                    }
                    catch
                    {
                        imageFailureCount++;
                        character.ImageFileName = string.Empty;
                    }
                }

                _library.Add(character);
                lastAddedCharacterId = character.Id;
                addedCount++;
            }
        }
        finally
        {
            foreach (string temporaryPath in previews
                         .Select(preview => preview.DownloadedImagePath)
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
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
                    // 임시 이미지 삭제 실패가 등록 결과를 막지는 않습니다.
                }
            }
        }

        MarkLibraryIndexesDirty();
        DeckDataService.SynchronizeSharedGroupInclusions(_library);
        RefreshGroupOptions();
        RefreshAllLists(lastAddedCharacterId);

        if (!string.IsNullOrWhiteSpace(lastAddedCharacterId))
        {
            BeginEditing(lastAddedCharacterId);
        }

        var details = new List<string> { $"추가 {addedCount}개" };
        if (duplicateCount > 0)
        {
            details.Add($"중복 건너뜀 {duplicateCount}개");
        }
        if (invalidCount > 0)
        {
            details.Add($"정보 부족 {invalidCount}개");
        }
        if (imageFailureCount > 0)
        {
            details.Add($"이미지 저장 실패 {imageFailureCount}개");
        }

        EditorStatusText.Text = "연속 자동 등록 완료 · " + string.Join(" · ", details) +
            (addedCount > 0 ? " · 자동 저장되었습니다." : string.Empty);
        EditorStatusText.Foreground = addedCount > 0
            ? BrushFromHex("#8FE3B1")
            : BrushFromHex("#FFD08A");
    }

    private void ClearEditorForNewCharacter()
    {
        _editingCharacterId = null;
        _isRelatedFormEditorOpen = false;
        RelatedFormEditorPanel.Visibility = Visibility.Collapsed;
        ToggleRelatedFormEditorButton.Content = "연결 설정";
        ToggleRelatedFormEditorButton.IsEnabled = false;
        _isRefreshingLists = true;
        CharacterLibraryListBox.SelectedItem = null;
        _isRefreshingLists = false;
        CharacterNameTextBox.Clear();
        CharacterSearchAliasesTextBox.Clear();
        CharacterCategoryComboBox.SelectedItem = CharacterCategories.Other;
        CharacterAttributeComboBox.SelectedItem = "미입력";
        CharacterSubAttributesTextBox.Clear();
        CharacterSpeciesComboBox.SelectedItem = "미입력";
        CharacterFavoriteCheckBox.IsChecked = false;
        CharacterBelovedCheckBox.IsChecked = false;
        CharacterLettersTextBox.Clear();
        RefreshGroupOptions();
        CharacterGroupComboBox.Text = string.Empty;
        CharacterIncludedGroupsTextBox.Clear();
        MiracleLeaderEffectCheckBox.IsChecked = false;
        MiracleTargetGroupsTextBox.Clear();
        MiracleGrantedLettersTextBox.Clear();
        MiracleEffectNoteTextBox.Clear();
        DeckGroupLetterEffectCheckBox.IsChecked = false;
        DeckGroupConditionGroupsTextBox.Clear();
        DeckGroupConditionMinimumCountTextBox.Text = "2";
        DeckGroupConditionGrantedLettersTextBox.Clear();
        DeckGroupConditionNoteTextBox.Clear();
        _editingImageFileName = string.Empty;
        _pendingImageSourcePath = null;
        _removeImageRequested = false;
        UpdateCharacterImagePreview();
        RelatedFormComboBox.ItemsSource = Array.Empty<RelatedFormDisplayItem>();
        RelatedFormComboBox.SelectedIndex = -1;
        LinkRelatedFormButton.IsEnabled = false;
        UnlinkRelatedFormButton.IsEnabled = false;
        UpdateRelatedFormsSummary(null);
        _editingLetterStates = new List<CharacterLetterState>();
        _editingForms = new List<CharacterForm>();
        UpdateCharacterStateSummary();
        UpdateCharacterFormSummary();
        CharacterNameTextBox.Focus();
        UpdateCharacterButton.IsEnabled = false;
        AddCharacterButton.IsEnabled = true;
        EditorStatusText.Text = "새 캐릭터의 이름과 문자를 입력하세요.";
        EditorStatusText.Foreground = BrushFromHex("#AEB8C8");
    }

    private void RefreshRelatedFormCandidates(string? currentCharacterId = null)
    {
        string? currentId = currentCharacterId ?? _editingCharacterId;
        RelatedFormDisplayItem[] items = _library
            .Where(character => !string.Equals(character.Id, currentId, StringComparison.Ordinal))
            .OrderBy(character => CharacterCategories.GetSortOrder(character.Category))
            .ThenBy(character => character.Name, StringComparer.Ordinal)
            .ThenBy(character => character.Id, StringComparer.Ordinal)
            .Select(character => new RelatedFormDisplayItem(character))
            .ToArray();

        RelatedFormComboBox.ItemsSource = items;
        RelatedFormComboBox.SelectedIndex = items.Length > 0 ? 0 : -1;

        CharacterEntry? current = FindCharacter(currentId);
        LinkRelatedFormButton.IsEnabled = current is not null && items.Length > 0;
        UnlinkRelatedFormButton.IsEnabled = current is not null &&
            !string.IsNullOrWhiteSpace(current.DeckRestrictionGroupId);
    }

    private void UpdateRelatedFormsSummary(CharacterEntry? character)
    {
        if (character is null)
        {
            RelatedFormsSummaryText.Text = "캐릭터를 먼저 추가하거나 선택하세요.";
            RelatedFormsSummaryText.Foreground = BrushFromHex("#8D98AA");
            return;
        }

        CharacterEntry[] related = GetRelatedForms(character).ToArray();
        if (related.Length == 0)
        {
            RelatedFormsSummaryText.Text = "연결된 다른 형태 없음";
            RelatedFormsSummaryText.Foreground = BrushFromHex("#8D98AA");
            return;
        }

        RelatedFormsSummaryText.Text = "모드시프트: " +
            string.Join(" · ", related.Select(item => item.Name));
        RelatedFormsSummaryText.Foreground = BrushFromHex("#B8EAF5");
    }

    private IEnumerable<CharacterEntry> GetRelatedForms(CharacterEntry character)
    {
        return _relatedFormsCache.TryGetValue(character.Id, out CharacterEntry[]? related)
            ? related
            : Array.Empty<CharacterEntry>();
    }

    private bool RelatedFormNamesContain(CharacterEntry character, string filter)
        => GetRelatedForms(character).Any(related =>
            related.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private void ToggleRelatedFormEditorButton_Click(object sender, RoutedEventArgs e)
    {
        CharacterEntry? current = FindCharacter(_editingCharacterId);
        if (current is null)
        {
            SetError("모드시프트를 설정할 캐릭터를 먼저 선택하세요.");
            return;
        }

        _isRelatedFormEditorOpen = !_isRelatedFormEditorOpen;
        RelatedFormEditorPanel.Visibility = _isRelatedFormEditorOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToggleRelatedFormEditorButton.Content = _isRelatedFormEditorOpen
            ? "설정 닫기"
            : "연결 설정";

        if (_isRelatedFormEditorOpen)
        {
            RefreshRelatedFormCandidates(current.Id);
        }
    }

    private void LinkRelatedFormButton_Click(object sender, RoutedEventArgs e)
    {
        CharacterEntry? current = FindCharacter(_editingCharacterId);
        if (current is null)
        {
            SetError("먼저 캐릭터를 목록에 추가하거나 선택하세요.");
            return;
        }

        if (RelatedFormComboBox.SelectedItem is not RelatedFormDisplayItem selected)
        {
            SetError("모드시프트로 연결할 다른 캐릭터를 선택하세요.");
            return;
        }

        CharacterEntry? target = FindCharacter(selected.Id);
        if (target is null || string.Equals(target.Id, current.Id, StringComparison.Ordinal))
        {
            SetError("연결할 캐릭터를 찾지 못했습니다.");
            return;
        }

        string currentGroup = NormalizeRestrictionGroup(current.DeckRestrictionGroupId);
        string targetGroup = NormalizeRestrictionGroup(target.DeckRestrictionGroupId);

        if (currentGroup.Length > 0 &&
            string.Equals(currentGroup, targetGroup, StringComparison.OrdinalIgnoreCase))
        {
            EditorStatusText.Text = $"'{current.Name}'과 '{target.Name}'은 이미 모드시프트로 연결되어 있습니다.";
            EditorStatusText.Foreground = BrushFromHex("#B8EAF5");
            return;
        }

        var groupsToMerge = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (currentGroup.Length > 0)
        {
            groupsToMerge.Add(currentGroup);
        }
        if (targetGroup.Length > 0)
        {
            groupsToMerge.Add(targetGroup);
        }

        var members = _library
            .Where(character =>
                string.Equals(character.Id, current.Id, StringComparison.Ordinal) ||
                string.Equals(character.Id, target.Id, StringComparison.Ordinal) ||
                groupsToMerge.Contains(NormalizeRestrictionGroup(character.DeckRestrictionGroupId)))
            .DistinctBy(character => character.Id, StringComparer.Ordinal)
            .ToList();

        int deckMemberCount = members.Count(member =>
            _deckIds.Contains(member.Id, StringComparer.Ordinal));
        if (deckMemberCount > 1)
        {
            SetError("연결하려는 형태가 현재 덱에 둘 이상 있습니다. 같은 카드 형태 중 한 명만 남긴 뒤 연결하세요.");
            return;
        }

        string mergedGroup = currentGroup.Length > 0
            ? currentGroup
            : targetGroup.Length > 0
                ? targetGroup
                : $"same-card-{Guid.NewGuid():N}";

        foreach (CharacterEntry member in members)
        {
            member.DeckRestrictionGroupId = mergedGroup;
        }
        MarkLibraryIndexesDirty(groupOptionsChanged: false);

        int selectedDeckIndex = GetSelectedDeckIndexForCharacter(current.Id);
        RefreshAllLists(current.Id, selectedDeckIndex);
        BeginEditing(current.Id);
        EditorStatusText.Text = $"'{current.Name}'과 '{target.Name}'을 같은 카드 형태로 연결했습니다. 두 형태는 한 덱에 동시에 넣을 수 없습니다.";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
    }

    private void UnlinkRelatedFormButton_Click(object sender, RoutedEventArgs e)
    {
        CharacterEntry? current = FindCharacter(_editingCharacterId);
        if (current is null)
        {
            SetError("연결을 해제할 캐릭터를 먼저 선택하세요.");
            return;
        }

        string group = NormalizeRestrictionGroup(current.DeckRestrictionGroupId);
        if (group.Length == 0)
        {
            SetError($"'{current.Name}'은 연결된 모드시프트 형태가 없습니다.");
            return;
        }

        CharacterEntry[] relatedBefore = GetRelatedForms(current).ToArray();
        current.DeckRestrictionGroupId = string.Empty;
        ClearRestrictionGroupWhenOnlyOneMemberRemains(group);
        MarkLibraryIndexesDirty(groupOptionsChanged: false);

        int selectedDeckIndex = GetSelectedDeckIndexForCharacter(current.Id);
        RefreshAllLists(current.Id, selectedDeckIndex);
        BeginEditing(current.Id);
        EditorStatusText.Text = relatedBefore.Length == 0
            ? $"'{current.Name}'의 이전 모드시프트 그룹 정보를 정리했습니다."
            : $"'{current.Name}'을 모드시프트 연결에서 분리했습니다.";
        EditorStatusText.Foreground = BrushFromHex("#FFD08A");
    }

    private void ClearRestrictionGroupWhenOnlyOneMemberRemains(string? restrictionGroupId)
    {
        string group = NormalizeRestrictionGroup(restrictionGroupId);
        if (group.Length == 0)
        {
            return;
        }

        CharacterEntry[] remaining = _library
            .Where(character => string.Equals(
                NormalizeRestrictionGroup(character.DeckRestrictionGroupId),
                group,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (remaining.Length <= 1)
        {
            foreach (CharacterEntry character in remaining)
            {
                character.DeckRestrictionGroupId = string.Empty;
            }
        }
    }

    private static string NormalizeRestrictionGroup(string? value)
        => (value ?? string.Empty).Trim();

    private void ApplySharedGroupInclusionsToLibrary(
        string? groupName,
        IEnumerable<string>? includedGroups)
    {
        string normalizedGroup = DeckDataService.NormalizeGroupName(groupName);
        if (normalizedGroup.Length == 0)
        {
            return;
        }

        List<string> normalizedIncluded = DeckDataService.NormalizeGroupNames(includedGroups)
            .Where(group => !string.Equals(
                group,
                normalizedGroup,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (CharacterEntry character in _library.Where(character =>
                     string.Equals(
                         DeckDataService.NormalizeGroupName(character.GroupName),
                         normalizedGroup,
                         StringComparison.OrdinalIgnoreCase)))
        {
            character.IncludedGroups = normalizedIncluded.ToList();
        }
    }

    private void AddCharacterButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadEditorValues(
                out string name,
                out List<string> letters,
                out string category,
                out bool isFavorite,
                out string groupName,
                out List<string> includedGroups,
                out MiracleLeaderEffect miracleLeaderEffect,
                out List<CharacterLetterState> letterStates))
        {
            return;
        }

        bool exactDuplicate = _library.Any(character =>
            string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase) &&
            character.Letters.SequenceEqual(letters, StringComparer.Ordinal));
        if (exactDuplicate)
        {
            SetError("같은 이름과 문자를 가진 캐릭터가 이미 등록되어 있습니다.");
            return;
        }

        var character = new CharacterEntry
        {
            Id = $"character-{Guid.NewGuid():N}",
            Name = name,
            SearchAliases = ParseSearchAliases(CharacterSearchAliasesTextBox.Text),
            Category = category,
            Attribute = ReadMetadataCombo(CharacterAttributeComboBox, isAttribute: true),
            SubAttributes = ParseSubAttributes(CharacterSubAttributesTextBox.Text, ReadMetadataCombo(CharacterAttributeComboBox, isAttribute: true)),
            Species = ReadMetadataCombo(CharacterSpeciesComboBox, isAttribute: false),
            GroupName = groupName,
            IncludedGroups = includedGroups,
            IsFavorite = isFavorite,
            IsBeloved = CharacterBelovedCheckBox.IsChecked == true,
            Letters = letters,
            DeckRestrictionGroupId = string.Empty,
            MiracleLeaderEffect = miracleLeaderEffect,
            DeckGroupLetterEffect = BuildDeckGroupLetterEffectFromEditor(),
            LetterStates = letterStates,
            AlternateForms = DeckDataService.NormalizeCharacterForms(_editingForms)
                .Select(CharacterLibraryService.CloneForm)
                .ToList()
        };

        if (!TryApplyPendingImage(character))
        {
            return;
        }

        _library.Add(character);
        MarkLibraryIndexesDirty();
        ApplySharedGroupInclusionsToLibrary(character.GroupName, character.IncludedGroups);
        DeckDataService.SynchronizeSharedGroupInclusions(_library);
        _editingCharacterId = character.Id;
        RefreshGroupOptions(character.GroupName);
        RefreshAllLists(character.Id);
        BeginEditing(character.Id);
        EditorStatusText.Text = $"'{character.Name}'을 캐릭터 목록에 추가했습니다. 자동 저장되었습니다.";
    }

    private void UpdateCharacterButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_editingCharacterId))
        {
            SetError("수정할 캐릭터를 목록에서 먼저 선택하세요.");
            return;
        }

        if (!TryUpdateEditingCharacter(showSuccessMessage: true))
        {
            return;
        }
    }

    private bool TryUpdateEditingCharacter(bool showSuccessMessage)
    {
        CharacterEntry? character = FindCharacter(_editingCharacterId);
        if (character is null)
        {
            SetError("수정할 캐릭터를 찾지 못했습니다.");
            return false;
        }

        if (!TryReadEditorValues(
                out string name,
                out List<string> letters,
                out string category,
                out bool isFavorite,
                out string groupName,
                out List<string> includedGroups,
                out MiracleLeaderEffect miracleLeaderEffect,
                out List<CharacterLetterState> letterStates))
        {
            return false;
        }

        bool exactDuplicate = _library.Any(other =>
            !string.Equals(other.Id, character.Id, StringComparison.Ordinal) &&
            string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase) &&
            other.Letters.SequenceEqual(letters, StringComparer.Ordinal));
        if (exactDuplicate)
        {
            SetError("같은 이름과 문자를 가진 다른 캐릭터가 이미 등록되어 있습니다.");
            return false;
        }

        if (!TryApplyPendingImage(character))
        {
            return false;
        }

        character.Name = name;
        character.SearchAliases = ParseSearchAliases(CharacterSearchAliasesTextBox.Text);
        character.Category = category;
        character.Attribute = ReadMetadataCombo(CharacterAttributeComboBox, isAttribute: true);
        character.SubAttributes = ParseSubAttributes(CharacterSubAttributesTextBox.Text, character.Attribute);
        character.Species = ReadMetadataCombo(CharacterSpeciesComboBox, isAttribute: false);
        character.GroupName = groupName;
        character.IncludedGroups = includedGroups;
        character.IsFavorite = isFavorite;
        character.IsBeloved = CharacterBelovedCheckBox.IsChecked == true;
        character.Letters = letters;
        character.MiracleLeaderEffect = miracleLeaderEffect;
        character.DeckGroupLetterEffect = BuildDeckGroupLetterEffectFromEditor();
        character.LetterStates = letterStates;
        character.AlternateForms = DeckDataService.NormalizeCharacterForms(_editingForms)
            .Select(CharacterLibraryService.CloneForm)
            .ToList();
        ApplySharedGroupInclusionsToLibrary(character.GroupName, character.IncludedGroups);
        DeckDataService.SynchronizeSharedGroupInclusions(_library);
        MarkLibraryIndexesDirty();

        int selectedDeckIndex = GetSelectedDeckIndexForCharacter(character.Id);
        RefreshGroupOptions(character.GroupName);
        RefreshAllLists(character.Id, selectedDeckIndex);
        BeginEditing(character.Id);

        if (showSuccessMessage)
        {
            EditorStatusText.Text = $"'{character.Name}' 정보를 수정했습니다. 덱에도 자동 반영됩니다.";
            EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
        }

        return true;
    }

    private void DeleteCharacterButton_Click(object sender, RoutedEventArgs e)
    {
        string? characterId = GetSelectedLibraryCharacterId() ?? _editingCharacterId;
        CharacterEntry? character = FindCharacter(characterId);
        if (character is null)
        {
            SetError("삭제할 캐릭터를 목록에서 먼저 선택하세요.");
            return;
        }

        bool isInDeck = _deckIds.Contains(character.Id, StringComparer.Ordinal);
        string deckNotice = isInDeck
            ? "\n\n현재 덱에서도 함께 제거됩니다."
            : string.Empty;

        MessageBoxResult result = MessageBox.Show(
            $"'{character.Name}'을 캐릭터 목록에서 삭제할까요?{deckNotice}",
            "캐릭터 삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        string removedRestrictionGroup = character.DeckRestrictionGroupId;
        CharacterImageService.DeleteImage(_dataDirectory, character.ImageFileName);
        foreach (CharacterForm form in character.AlternateForms ?? new List<CharacterForm>())
        {
            CharacterImageService.DeleteImage(_dataDirectory, form.ImageFileName);
        }
        InvalidateCharacterThumbnail(character.Id);
        _library.Remove(character);
        MarkLibraryIndexesDirty();
        ClearRestrictionGroupWhenOnlyOneMemberRemains(removedRestrictionGroup);
        _deckIds.RemoveAll(id => string.Equals(id, character.Id, StringComparison.Ordinal));
        foreach (DeckPreset preset in _presets)
        {
            preset.CharacterIds.RemoveAll(id => string.Equals(id, character.Id, StringComparison.Ordinal));
        }
        ClearEditorForNewCharacter();
        RefreshGroupOptions();
        RefreshPresetList(_selectedPresetId);
        RefreshAllLists();
        EditorStatusText.Text = $"'{character.Name}'을 목록과 덱에서 제거했습니다. 저장된 프리셋에서도 제거했습니다.";
        EditorStatusText.Foreground = BrushFromHex("#FFD08A");
    }

    private void LibraryDeckToggleButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button button || button.Tag is not string characterId)
        {
            return;
        }

        int deckIndex = _deckIds.FindIndex(id =>
            string.Equals(id, characterId, StringComparison.Ordinal));
        if (deckIndex >= 0)
        {
            RemoveCharacterFromDeckById(characterId);
            return;
        }

        AddCharacterToDeck(characterId);
    }

    private void RemoveCharacterFromDeckById(string characterId)
    {
        int deckIndex = _deckIds.FindIndex(id =>
            string.Equals(id, characterId, StringComparison.Ordinal));
        if (deckIndex < 0)
        {
            return;
        }

        CharacterEntry? character = FindCharacter(characterId);
        _deckIds.RemoveAt(deckIndex);
        RefreshAllLists(characterId, Math.Min(deckIndex, _deckIds.Count - 1));
        EditorStatusText.Text = character is null
            ? "선택한 캐릭터를 현재 덱에서 뺐습니다."
            : $"'{character.Name}'을 현재 덱에서 뺐습니다. 캐릭터 목록에는 남아 있습니다.";
        EditorStatusText.Foreground = BrushFromHex("#FFD08A");
    }

    private void AddSelectedToDeckButton_Click(object sender, RoutedEventArgs e)
    {
        string? characterId = GetSelectedLibraryCharacterId() ?? _editingCharacterId;
        if (string.IsNullOrWhiteSpace(characterId))
        {
            SetError("덱에 추가할 캐릭터를 목록에서 먼저 선택하세요.");
            return;
        }

        AddCharacterToDeck(characterId);
    }

    private void AddCharacterToDeck(string characterId)
    {
        CharacterEntry? character = FindCharacter(characterId);
        if (character is null)
        {
            SetError("덱에 추가할 캐릭터를 찾지 못했습니다.");
            return;
        }

        if (_deckIds.Contains(character.Id, StringComparer.Ordinal))
        {
            SetError($"'{character.Name}'은 이미 현재 덱에 있습니다.");
            return;
        }

        if (HasRestrictionConflict(character.Id, character.DeckRestrictionGroupId, out CharacterEntry? conflict))
        {
            SetError($"'{character.Name}'은 '{conflict!.Name}'과 같은 모드시프트 그룹이라 함께 넣을 수 없습니다.");
            return;
        }

        if (_deckIds.Count >= MaximumDeckSize)
        {
            SetError("현재 덱은 최대 12명입니다. 기존 캐릭터를 먼저 빼세요.");
            return;
        }

        _deckIds.Add(character.Id);
        RefreshAllLists(character.Id, _deckIds.Count - 1);
        EditorStatusText.Text = $"'{character.Name}'을 현재 덱 {_deckIds.Count}번에 추가했습니다.";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
    }

    private void RemoveFromDeckButton_Click(object sender, RoutedEventArgs e)
        => RemoveSelectedDeckCharacter();

    private void RemoveSelectedDeckCharacter()
    {
        if (DeckListBox.SelectedItem is not DeckDisplayItem item)
        {
            SetError("덱에서 뺄 캐릭터를 먼저 선택하세요.");
            return;
        }

        CharacterEntry? character = FindCharacter(item.Id);
        int selectedIndex = item.Index;
        _deckIds.RemoveAt(selectedIndex);
        RefreshAllLists(character?.Id ?? _editingCharacterId, Math.Min(selectedIndex, _deckIds.Count - 1));
        EditorStatusText.Text = character is null
            ? "선택한 캐릭터를 현재 덱에서 뺐습니다."
            : $"'{character.Name}'을 현재 덱에서 뺐습니다. 캐릭터 목록에는 남아 있습니다.";
        EditorStatusText.Foreground = BrushFromHex("#FFD08A");
    }

    private void ImportDeckFromScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitPendingEditorChanges())
        {
            return;
        }

        var window = new DeckScreenshotImportWindow(_library, _dataDirectory)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        string[] requestedIds = window.SelectedCharacterIds
            .Where(id => _characterById.ContainsKey(id))
            .Take(MaximumDeckSize)
            .ToArray();
        List<string> normalizedIds = NormalizeDeckIdsForRestrictions(requestedIds);

        _deckIds.Clear();
        _deckIds.AddRange(normalizedIds);
        RefreshAllLists(_editingCharacterId);

        int skippedCount = requestedIds.Length - normalizedIds.Count;
        EditorStatusText.Text = skippedCount > 0
            ? $"덱 스크린샷에서 {_deckIds.Count}명을 적용했습니다 · 중복/모드시프트 제한 {skippedCount}명 제외 · 자동 저장되었습니다."
            : $"덱 스크린샷에서 {_deckIds.Count}명을 적용했습니다 · 자동 저장되었습니다.";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
    }

    private void SetLeaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeckListBox.SelectedItem is not DeckDisplayItem item)
        {
            SetError("리더로 지정할 캐릭터를 현재 덱에서 먼저 선택하세요.");
            return;
        }

        if (item.Index == 0)
        {
            EditorStatusText.Text = $"'{item.Name}'은 이미 현재 덱의 리더입니다.";
            EditorStatusText.Foreground = BrushFromHex("#B8EAF5");
            return;
        }

        string characterId = _deckIds[item.Index];
        _deckIds.RemoveAt(item.Index);
        _deckIds.Insert(0, characterId);
        RefreshAllLists(characterId, 0);
        EditorStatusText.Text = $"'{item.Name}'을 덱 1번 리더로 지정했습니다.";
        EditorStatusText.Foreground = BrushFromHex("#D9C2FF");
    }

    private void MoveDeckUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeckListBox.SelectedItem is not DeckDisplayItem item || item.Index <= 0)
        {
            return;
        }

        (_deckIds[item.Index - 1], _deckIds[item.Index]) =
            (_deckIds[item.Index], _deckIds[item.Index - 1]);
        RefreshAllLists(item.Id, item.Index - 1);
    }

    private void MoveDeckDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeckListBox.SelectedItem is not DeckDisplayItem item ||
            item.Index < 0 ||
            item.Index >= _deckIds.Count - 1)
        {
            return;
        }

        (_deckIds[item.Index + 1], _deckIds[item.Index]) =
            (_deckIds[item.Index], _deckIds[item.Index + 1]);
        RefreshAllLists(item.Id, item.Index + 1);
    }

    private void ClearDeckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deckIds.Count == 0)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            "현재 덱의 12칸을 모두 비울까요?\n캐릭터 목록 자체는 삭제되지 않습니다.",
            "덱 비우기",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _deckIds.Clear();
        RefreshAllLists(_editingCharacterId);
        EditorStatusText.Text = "현재 덱을 비웠습니다. 캐릭터 목록은 그대로 유지됩니다.";
        EditorStatusText.Foreground = BrushFromHex("#FFD08A");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
        => SaveChangesToDisk(closeAfterSave: true);

    private bool SaveChangesToDisk(bool closeAfterSave)
    {
        if (!TryCommitPendingEditorChanges())
        {
            return false;
        }

        RemoveMissingDeckCharacters();
        RemoveMissingPresetCharacters();

        if (_library.Count == 0)
        {
            SetError("캐릭터 목록에 최소 한 명은 등록해야 합니다.");
            return false;
        }

        if (_deckIds.Count == 0)
        {
            SetError("현재 덱에 최소 한 명은 추가해야 합니다.");
            return false;
        }

        if (!ValidateCurrentDeckRestrictions(out string restrictionError))
        {
            SetError(restrictionError);
            return false;
        }

        if (_hasPendingAutoSave && !FlushPendingAutoSave(showErrorDialog: true))
        {
            return false;
        }

        if (!_lastPersistSucceeded && !PersistCurrentStateToDisk(showErrorDialog: true))
        {
            return false;
        }

        // 아무 변경 없이 '저장하고 적용'만 누른 경우에도 메인 화면이 정상적으로
        // 현재 덱을 다시 반영할 수 있도록 저장 완료 상태만 표시합니다.
        if (!_hasSavedToDisk)
        {
            SavedCharacters = _deckIds
                .Select(FindCharacter)
                .Where(character => character is not null)
                .Cast<CharacterEntry>()
                .Select(CharacterLibraryService.Clone)
                .ToList();
            _hasSavedToDisk = true;
        }

        if (closeAfterSave)
        {
            _isClosingAfterExplicitSave = true;
            DialogResult = true;
            return true;
        }

        EditorStatusText.Text = "현재 캐릭터 상세 정보와 덱 상태를 저장했습니다.";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
        return true;
    }

    private bool PersistCurrentStateToDisk(bool showErrorDialog)
    {
        if (_isPersistingState)
        {
            return true;
        }

        _isPersistingState = true;
        try
        {
            RemoveMissingDeckCharacters();
            RemoveMissingPresetCharacters();
            DeckDataService.SynchronizeSharedGroupInclusions(_library);

            // DeckDataService.Save 내부에서 저장용 정규화 객체를 새로 만들기 때문에
            // 여기서 2,500명 전체를 다시 Clone하면 메모리/CPU를 두 번 사용하게 됩니다.
            // 정렬된 참조 목록만 넘기고, 반환용 현재 덱 12명만 마지막에 복제합니다.
            List<CharacterEntry> orderedLibrary = _library
                .OrderByDescending(character => character.IsFavorite)
                .ThenBy(character => CharacterCategories.GetSortOrder(character.Category))
                .ThenBy(character => character.Name, StringComparer.Ordinal)
                .ThenBy(character => character.Id, StringComparer.Ordinal)
                .ToList();

            List<CharacterEntry> deckCharacters = _deckIds
                .Select(FindCharacter)
                .Where(character => character is not null)
                .Cast<CharacterEntry>()
                .ToList();

            DeckDataService.Save(_libraryPath, orderedLibrary);
            DeckDataService.Save(_deckPath, deckCharacters);
            DeckPresetService.Save(_presetPath, _presets);


            SavedCharacters = deckCharacters
                .Select(CharacterLibraryService.Clone)
                .ToList();
            _hasSavedToDisk = true;
            _lastPersistSucceeded = true;
            return true;
        }
        catch (Exception exception)
        {
            _lastPersistSucceeded = false;
            if (showErrorDialog)
            {
                MessageBox.Show(
                    $"캐릭터 목록 또는 덱을 저장하지 못했습니다.\n\n{exception.Message}",
                    "덱 저장 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            else
            {
                EditorStatusText.Text = $"자동 저장 실패: {exception.Message}";
                EditorStatusText.Foreground = BrushFromHex("#FF8C8C");
            }

            return false;
        }
        finally
        {
            _isPersistingState = false;
        }
    }

    private void AutoSaveCommittedStateSafely()
    {
        if (!_autoSaveReady)
        {
            return;
        }

        // 연속 수정 중에는 characters.json 전체를 매번 다시 쓰지 않고
        // 마지막 변경 후 짧게 쉬었을 때 한 번만 저장합니다.
        _hasPendingAutoSave = true;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (!_hasPendingAutoSave || _isPersistingState)
        {
            return;
        }

        _hasPendingAutoSave = false;
        PersistCurrentStateToDisk(showErrorDialog: false);
    }

    private bool FlushPendingAutoSave(bool showErrorDialog)
    {
        _autoSaveTimer.Stop();
        if (!_hasPendingAutoSave)
        {
            return _lastPersistSucceeded;
        }

        _hasPendingAutoSave = false;
        return PersistCurrentStateToDisk(showErrorDialog);
    }

    private void DeckEditorWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosingAfterExplicitSave)
        {
            return;
        }

        // X 버튼, Alt+F4, '닫기' 모두 동일하게 마지막 편집 내용을 먼저 반영한 뒤 저장합니다.
        // 입력값이 불완전해서 캐릭터로 확정할 수 없는 경우에는 창을 닫지 않아
        // 사용자가 실수로 작업 내용을 버리는 일을 막습니다.
        if (!TryCommitPendingEditorChanges())
        {
            e.Cancel = true;
            return;
        }

        if (_hasPendingAutoSave && !FlushPendingAutoSave(showErrorDialog: true))
        {
            e.Cancel = true;
            return;
        }

        if (!_lastPersistSucceeded && !PersistCurrentStateToDisk(showErrorDialog: true))
        {
            e.Cancel = true;
        }
    }

    private bool TryCommitPendingEditorChanges()
    {
        string rawName = CharacterNameTextBox.Text.Trim();
        List<string> rawSearchAliases = ParseSearchAliases(CharacterSearchAliasesTextBox.Text);
        string rawLetters = CharacterLettersTextBox.Text.Trim();
        string rawCategory = CharacterCategories.Normalize(CharacterCategoryComboBox.SelectedItem as string);
        string rawAttribute = ReadMetadataCombo(CharacterAttributeComboBox, isAttribute: true);
        List<string> rawSubAttributes = ParseSubAttributes(CharacterSubAttributesTextBox.Text, rawAttribute);
        string rawSpecies = ReadMetadataCombo(CharacterSpeciesComboBox, isAttribute: false);
        bool rawFavorite = CharacterFavoriteCheckBox.IsChecked == true;
        bool rawBeloved = CharacterBelovedCheckBox.IsChecked == true;
        string rawGroupName = DeckDataService.NormalizeGroupName(CharacterGroupComboBox.Text);
        List<string> rawIncludedGroups = ParseGroupNames(CharacterIncludedGroupsTextBox.Text)
            .Where(group => !string.Equals(group, rawGroupName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        string rawMiracleTargets = MiracleTargetGroupsTextBox.Text.Trim();
        string rawMiracleLetters = MiracleGrantedLettersTextBox.Text.Trim();
        bool rawMiracleEnabled = rawMiracleTargets.Length > 0 && rawMiracleLetters.Length > 0;
        string rawMiracleNote = MiracleEffectNoteTextBox.Text.Trim();
        string rawDeckGroupTargets = DeckGroupConditionGroupsTextBox.Text.Trim();
        string rawDeckGroupCount = DeckGroupConditionMinimumCountTextBox.Text.Trim();
        string rawDeckGroupLetters = DeckGroupConditionGrantedLettersTextBox.Text.Trim();
        string rawDeckGroupNote = DeckGroupConditionNoteTextBox.Text.Trim();

        if (rawName.Length == 0 && rawSearchAliases.Count == 0 && rawLetters.Length == 0 && _editingLetterStates.Count == 0 &&
            _editingForms.Count == 0 && rawAttribute.Length == 0 && rawSubAttributes.Count == 0 && rawSpecies.Length == 0 &&
            rawGroupName.Length == 0 && rawIncludedGroups.Count == 0 &&
            !rawMiracleEnabled && rawMiracleTargets.Length == 0 &&
            rawMiracleLetters.Length == 0 && rawMiracleNote.Length == 0 &&
            rawDeckGroupTargets.Length == 0 && rawDeckGroupLetters.Length == 0 &&
            rawDeckGroupNote.Length == 0 && (rawDeckGroupCount.Length == 0 || rawDeckGroupCount == "2"))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(_editingCharacterId))
        {
            CharacterEntry? character = FindCharacter(_editingCharacterId);
            if (character is null)
            {
                return true;
            }

            List<string> parsedLetters = ParseLetters(rawLetters);
            bool changed = !string.Equals(character.Name, rawName, StringComparison.Ordinal) ||
                           !DeckDataService.NormalizeSearchAliases(character.SearchAliases)
                               .SequenceEqual(rawSearchAliases, StringComparer.OrdinalIgnoreCase) ||
                           !string.Equals(
                               CharacterCategories.Normalize(character.Category),
                               rawCategory,
                               StringComparison.Ordinal) ||
                           !string.Equals(NormalizeAttribute(character.Attribute), rawAttribute, StringComparison.Ordinal) ||
                           !DeckDataService.NormalizeAttributes(character.SubAttributes, character.Attribute)
                               .OrderBy(value => value, StringComparer.Ordinal)
                               .SequenceEqual(rawSubAttributes.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal) ||
                           !string.Equals(NormalizeSpecies(character.Species), rawSpecies, StringComparison.Ordinal) ||
                           character.IsFavorite != rawFavorite ||
                           character.IsBeloved != rawBeloved ||
                           !string.Equals(
                               DeckDataService.NormalizeGroupName(character.GroupName),
                               rawGroupName,
                               StringComparison.Ordinal) ||
                           !DeckDataService.NormalizeGroupNames(character.IncludedGroups)
                               .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
                               .SequenceEqual(
                                   rawIncludedGroups.OrderBy(group => group, StringComparer.OrdinalIgnoreCase),
                                   StringComparer.OrdinalIgnoreCase) ||
                           !character.Letters.SequenceEqual(parsedLetters, StringComparer.Ordinal) ||
                           !MiracleEffectsEqual(
                               character.MiracleLeaderEffect,
                               BuildMiracleEffectFromEditor()) ||
                           !DeckGroupLetterEffectsEqual(
                               character.DeckGroupLetterEffect,
                               BuildDeckGroupLetterEffectFromEditor()) ||
                           !LetterStatesEqual(character.LetterStates, _editingLetterStates) ||
                           !CharacterFormsEqual(character.AlternateForms, _editingForms) ||
                           _removeImageRequested ||
                           !string.IsNullOrWhiteSpace(_pendingImageSourcePath);
            return !changed || TryUpdateEditingCharacter(showSuccessMessage: false);
        }

        if (!TryReadEditorValues(
                out string name,
                out List<string> letters,
                out string category,
                out bool isFavorite,
                out string groupName,
                out List<string> includedGroups,
                out MiracleLeaderEffect miracleLeaderEffect,
                out List<CharacterLetterState> letterStates))
        {
            return false;
        }

        bool exactDuplicate = _library.Any(character =>
            string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase) &&
            character.Letters.SequenceEqual(letters, StringComparer.Ordinal));
        if (exactDuplicate)
        {
            SetError("입력 중인 새 캐릭터가 기존 목록과 중복됩니다.");
            return false;
        }

        var newCharacter = new CharacterEntry
        {
            Id = $"character-{Guid.NewGuid():N}",
            Name = name,
            SearchAliases = ParseSearchAliases(CharacterSearchAliasesTextBox.Text),
            Category = category,
            Attribute = ReadMetadataCombo(CharacterAttributeComboBox, isAttribute: true),
            SubAttributes = ParseSubAttributes(CharacterSubAttributesTextBox.Text, ReadMetadataCombo(CharacterAttributeComboBox, isAttribute: true)),
            Species = ReadMetadataCombo(CharacterSpeciesComboBox, isAttribute: false),
            GroupName = groupName,
            IncludedGroups = includedGroups,
            IsFavorite = isFavorite,
            IsBeloved = CharacterBelovedCheckBox.IsChecked == true,
            Letters = letters,
            DeckRestrictionGroupId = string.Empty,
            MiracleLeaderEffect = miracleLeaderEffect,
            DeckGroupLetterEffect = BuildDeckGroupLetterEffectFromEditor(),
            LetterStates = letterStates,
            AlternateForms = DeckDataService.NormalizeCharacterForms(_editingForms)
                .Select(CharacterLibraryService.CloneForm)
                .ToList()
        };

        if (!TryApplyPendingImage(newCharacter))
        {
            return false;
        }

        _library.Add(newCharacter);
        MarkLibraryIndexesDirty();
        ApplySharedGroupInclusionsToLibrary(newCharacter.GroupName, newCharacter.IncludedGroups);
        DeckDataService.SynchronizeSharedGroupInclusions(_library);
        // X/닫기를 누르는 순간 입력 중이던 신규 캐릭터가 확정된 경우에도
        // 닫기 전에 즉시 자동 저장해 데이터가 유실되지 않게 합니다.
        AutoSaveCommittedStateSafely();
        return _lastPersistSucceeded;
    }

    private bool TryReadEditorValues(
        out string name,
        out List<string> letters,
        out string category,
        out bool isFavorite,
        out string groupName,
        out List<string> includedGroups,
        out MiracleLeaderEffect miracleLeaderEffect,
        out List<CharacterLetterState> letterStates)
    {
        name = CharacterNameTextBox.Text.Trim();
        letters = ParseLetters(CharacterLettersTextBox.Text);
        category = CharacterCategories.Normalize(CharacterCategoryComboBox.SelectedItem as string);
        isFavorite = CharacterFavoriteCheckBox.IsChecked == true;
        string normalizedGroupName = DeckDataService.NormalizeGroupName(CharacterGroupComboBox.Text);
        groupName = normalizedGroupName;
        includedGroups = ParseGroupNames(CharacterIncludedGroupsTextBox.Text)
            .Where(group => !string.Equals(group, normalizedGroupName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        miracleLeaderEffect = BuildMiracleEffectFromEditor();
        letterStates = DeckDataService.NormalizeLetterStates(_editingLetterStates)
            .Select(CharacterLibraryService.CloneState)
            .ToList();

        if (name.Length == 0)
        {
            SetError("캐릭터 이름을 입력하세요.");
            CharacterNameTextBox.Focus();
            return false;
        }

        if (letters.Count == 0 && letterStates.Count == 0 && _editingForms.Count == 0)
        {
            SetError("기본 문자, 동일명 형태 또는 문자 상태를 한 개 이상 등록하세요.");
            CharacterLettersTextBox.Focus();
            return false;
        }

        if (includedGroups.Count > 0 && groupName.Length == 0)
        {
            SetError("같이 취급되는 그룹을 사용하려면 먼저 기본 소속 그룹을 입력하세요.");
            CharacterGroupComboBox.Focus();
            return false;
        }

        // 미라클 부여 문자가 입력된 경우에만 완전한 미라클 규칙을 요구합니다.
        // 대상 그룹만 남아 있던 v1.13 이전 데이터는 그룹 포함 규칙으로 자동 이전됩니다.
        if (miracleLeaderEffect.GrantedLetters.Count > 0 &&
            miracleLeaderEffect.TargetGroups.Count == 0)
        {
            SetError("미라클 리더 효과의 대상 그룹을 한 개 이상 입력하세요.");
            MiracleTargetGroupsTextBox.Focus();
            return false;
        }

        if (miracleLeaderEffect.GrantedLetters.Count == 0)
        {
            miracleLeaderEffect = new MiracleLeaderEffect();
        }

        DeckGroupLetterEffect deckGroupEffect = BuildDeckGroupLetterEffectFromEditor();
        bool hasDeckGroupInput = DeckGroupConditionGroupsTextBox.Text.Trim().Length > 0 ||
                                 DeckGroupConditionGrantedLettersTextBox.Text.Trim().Length > 0 ||
                                 DeckGroupConditionNoteTextBox.Text.Trim().Length > 0;
        if (hasDeckGroupInput)
        {
            if (deckGroupEffect.TargetGroups.Count == 0)
            {
                SetError("덱 그룹 조건 문자의 조건 그룹을 한 개 이상 입력하세요.");
                DeckGroupConditionGroupsTextBox.Focus();
                return false;
            }

            if (deckGroupEffect.GrantedLetters.Count == 0)
            {
                SetError("덱 그룹 조건 달성 시 추가되는 문자를 입력하세요.");
                DeckGroupConditionGrantedLettersTextBox.Focus();
                return false;
            }

            if (!int.TryParse(DeckGroupConditionMinimumCountTextBox.Text.Trim(), out int minimumCount) || minimumCount < 1 || minimumCount > MaximumDeckSize)
            {
                SetError($"최소 인원은 1~{MaximumDeckSize} 사이의 숫자로 입력하세요.");
                DeckGroupConditionMinimumCountTextBox.Focus();
                return false;
            }
        }

        return true;
    }

    private void ChooseCharacterImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "캐릭터 이미지 선택",
            Filter = CharacterImageService.GetDialogFilter(),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!CharacterImageService.IsSupportedImageFile(dialog.FileName))
        {
            SetError($"지원하지 않는 이미지 형식입니다. {CharacterImageService.GetSupportedFormatText()} 파일을 선택하세요.");
            return;
        }

        if (CharacterImageService.LoadBitmapFromPath(dialog.FileName, 160) is null)
        {
            SetError("선택한 이미지를 읽을 수 없습니다. 파일이 손상되었거나 지원하지 않는 이미지 형식인지 확인해 주세요.");
            return;
        }

        _pendingImageSourcePath = dialog.FileName;
        _removeImageRequested = false;
        UpdateCharacterImagePreview();
        EditorStatusText.Text = "이미지를 선택했습니다. 저장할 때 표준 PNG로 변환해 Data/CharacterImages 폴더에 저장합니다.";
        EditorStatusText.Foreground = BrushFromHex("#B8EAF5");
    }

    private void RemoveCharacterImageButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingImageSourcePath = null;
        _removeImageRequested = true;
        _editingImageFileName = string.Empty;
        UpdateCharacterImagePreview();
        EditorStatusText.Text = "캐릭터 이미지를 제거하도록 표시했습니다. 수정 버튼 또는 창 닫기 시 자동 반영됩니다.";
        EditorStatusText.Foreground = BrushFromHex("#FFD08A");
    }

    private void UpdateCharacterImagePreview()
    {
        var bitmap = !string.IsNullOrWhiteSpace(_pendingImageSourcePath)
            ? CharacterImageService.LoadBitmapFromPath(_pendingImageSourcePath, 160)
            : CharacterImageService.LoadBitmap(_dataDirectory, _editingImageFileName, 160);

        CharacterImagePreview.Source = bitmap;
        CharacterImagePreview.Visibility = bitmap is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        CharacterImagePlaceholder.Visibility = bitmap is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(_pendingImageSourcePath))
        {
            CharacterImageFileText.Text = $"선택됨: {Path.GetFileName(_pendingImageSourcePath)}";
            CharacterImageFileText.Foreground = BrushFromHex("#B8EAF5");
        }
        else if (!string.IsNullOrWhiteSpace(_editingImageFileName) && bitmap is not null)
        {
            CharacterImageFileText.Text = _editingImageFileName;
            CharacterImageFileText.Foreground = BrushFromHex("#8FE3B1");
        }
        else
        {
            CharacterImageFileText.Text = "등록된 이미지 없음";
            CharacterImageFileText.Foreground = BrushFromHex("#8D98AA");
        }
    }

    private bool TryApplyPendingImage(CharacterEntry character)
    {
        try
        {
            if (_removeImageRequested)
            {
                CharacterImageService.DeleteImage(_dataDirectory, character.ImageFileName);
                character.ImageFileName = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(_pendingImageSourcePath))
            {
                character.ImageFileName = CharacterImageService.SaveImageCopy(
                    _pendingImageSourcePath,
                    _dataDirectory,
                    character.Id,
                    character.ImageFileName);
            }

            InvalidateCharacterThumbnail(character.Id);
            _editingImageFileName = Path.GetFileName(character.ImageFileName ?? string.Empty);
            _pendingImageSourcePath = null;
            _removeImageRequested = false;
            UpdateCharacterImagePreview();
            return true;
        }
        catch (Exception exception)
        {
            SetError($"캐릭터 이미지를 저장하지 못했습니다: {exception.Message}");
            return false;
        }
    }

    private void EditLetterStatesButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new LetterStateEditorWindow(
            CharacterNameTextBox.Text.Trim(),
            _editingLetterStates)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        _editingLetterStates = window.SavedStates
            .Select(CharacterLibraryService.CloneState)
            .ToList();
        UpdateCharacterStateSummary();
        EditorStatusText.Text = $"문자 상태 {_editingLetterStates.Count}개를 편집했습니다. 캐릭터 수정 버튼을 누르거나 창을 닫으면 자동 반영됩니다.";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
    }

    private void UpdateCharacterStateSummary()
    {
        if (_editingLetterStates.Count == 0)
        {
            CharacterStateSummaryText.Text = "추가 문자 상태 없음 · 기본 문자만 사용";
            CharacterStateSummaryText.Foreground = BrushFromHex("#8D98AA");
            return;
        }

        CharacterStateSummaryText.Text = string.Join(
            "  /  ",
            _editingLetterStates.Select(state =>
                $"{state.Name}({CharacterLetterStateKinds.Normalize(state.Kind)} · {string.Join("·", state.Letters)})"));
        CharacterStateSummaryText.Foreground = BrushFromHex("#B8EAF5");
    }

    private void EditCharacterFormsButton_Click(object sender, RoutedEventArgs e)
    {
        string characterName = CharacterNameTextBox.Text.Trim();
        if (characterName.Length == 0)
        {
            SetError("동일명 형태를 편집할 캐릭터 이름을 먼저 입력하세요.");
            return;
        }

        string characterId = string.IsNullOrWhiteSpace(_editingCharacterId)
            ? $"pending-{Guid.NewGuid():N}"
            : _editingCharacterId;
        var window = new CharacterFormEditorWindow(
            characterId,
            characterName,
            _dataDirectory,
            _editingForms)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        _editingForms = window.SavedForms
            .Select(CharacterLibraryService.CloneForm)
            .ToList();
        UpdateCharacterFormSummary();
        InvalidateCharacterThumbnail(characterId);
        EditorStatusText.Text = $"동일 이름 모드시프트 형태 {_editingForms.Count}개를 편집했습니다. 캐릭터 수정 버튼을 누르거나 창을 닫으면 자동 반영됩니다.";
        EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
    }

    private void UpdateCharacterFormSummary()
    {
        if (_editingForms.Count == 0)
        {
            CharacterFormSummaryText.Text = "추가 형태 없음 · 기본 형태는 위의 문자와 이미지를 사용";
            CharacterFormSummaryText.Foreground = BrushFromHex("#8D98AA");
            return;
        }

        CharacterFormSummaryText.Text = $"기본 형태 + {_editingForms.Count}개 · " + string.Join(
            "  /  ",
            _editingForms.Select(form =>
            {
                string attribute = NormalizeAttribute(form.Attribute);
                string attributeText = string.Join("/", new[] { attribute }
                    .Concat(DeckDataService.NormalizeAttributes(form.SubAttributes, attribute))
                    .Where(value => value.Length > 0));
                string species = NormalizeSpecies(form.Species);
                string meta = string.Join("·", new[] { attributeText, species }.Where(value => value.Length > 0));
                return $"{form.Name}({string.Join("·", form.Letters)})" + (meta.Length > 0 ? $"[{meta}]" : string.Empty);
            }));
        CharacterFormSummaryText.Foreground = BrushFromHex("#B8EAF5");
    }

    private static bool CharacterFormsEqual(
        IReadOnlyList<CharacterForm>? first,
        IReadOnlyList<CharacterForm>? second)
    {
        List<CharacterForm> normalizedFirst = DeckDataService.NormalizeCharacterForms(first);
        List<CharacterForm> normalizedSecond = DeckDataService.NormalizeCharacterForms(second);
        if (normalizedFirst.Count != normalizedSecond.Count)
        {
            return false;
        }

        for (int index = 0; index < normalizedFirst.Count; index++)
        {
            CharacterForm left = normalizedFirst[index];
            CharacterForm right = normalizedSecond[index];
            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal) ||
                !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                !string.Equals(left.ImageFileName, right.ImageFileName, StringComparison.OrdinalIgnoreCase) ||
                !left.Letters.SequenceEqual(right.Letters, StringComparer.Ordinal) ||
                !string.Equals(DeckDataService.NormalizeAttribute(left.Attribute), DeckDataService.NormalizeAttribute(right.Attribute), StringComparison.Ordinal) ||
                !DeckDataService.NormalizeAttributes(left.SubAttributes, left.Attribute)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(
                        DeckDataService.NormalizeAttributes(right.SubAttributes, right.Attribute).OrderBy(value => value, StringComparer.Ordinal),
                        StringComparer.Ordinal) ||
                !string.Equals(DeckDataService.NormalizeSpecies(left.Species), DeckDataService.NormalizeSpecies(right.Species), StringComparison.Ordinal) ||
                !string.Equals(left.Note, right.Note, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LetterStatesEqual(
        IReadOnlyList<CharacterLetterState> first,
        IReadOnlyList<CharacterLetterState> second)
    {
        List<CharacterLetterState> normalizedFirst = DeckDataService.NormalizeLetterStates(first);
        List<CharacterLetterState> normalizedSecond = DeckDataService.NormalizeLetterStates(second);
        if (normalizedFirst.Count != normalizedSecond.Count)
        {
            return false;
        }

        for (int index = 0; index < normalizedFirst.Count; index++)
        {
            CharacterLetterState left = normalizedFirst[index];
            CharacterLetterState right = normalizedSecond[index];
            if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal) ||
                !string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                !string.Equals(left.Kind, right.Kind, StringComparison.Ordinal) ||
                left.IncludeBaseLetters != right.IncludeBaseLetters ||
                !left.Letters.SequenceEqual(right.Letters, StringComparer.Ordinal) ||
                !string.Equals(left.Note, right.Note, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private MiracleLeaderEffect BuildMiracleEffectFromEditor()
    {
        List<string> targetGroups = ParseGroupNames(MiracleTargetGroupsTextBox.Text);
        List<string> grantedLetters = ParseLetters(MiracleGrantedLettersTextBox.Text);
        bool isConfigured = targetGroups.Count > 0 && grantedLetters.Count > 0;
        return new MiracleLeaderEffect
        {
            // 대상 그룹과 부여 문자가 모두 있으면 자동으로 효과 보유로 판단합니다.
            IsEnabled = isConfigured,
            TargetGroups = targetGroups,
            GrantedLetters = grantedLetters,
            Note = MiracleEffectNoteTextBox.Text.Trim()
        };
    }

    private DeckGroupLetterEffect BuildDeckGroupLetterEffectFromEditor()
    {
        List<string> targetGroups = ParseGroupNames(DeckGroupConditionGroupsTextBox.Text);
        List<string> grantedLetters = ParseLetters(DeckGroupConditionGrantedLettersTextBox.Text);
        int minimumCount = int.TryParse(
            DeckGroupConditionMinimumCountTextBox.Text.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsedCount)
            ? Math.Clamp(parsedCount, 1, MaximumDeckSize)
            : 2;
        bool isConfigured = targetGroups.Count > 0 && grantedLetters.Count > 0;
        return new DeckGroupLetterEffect
        {
            IsEnabled = isConfigured,
            TargetGroups = targetGroups,
            MinimumCount = minimumCount,
            GrantedLetters = grantedLetters,
            Note = DeckGroupConditionNoteTextBox.Text.Trim()
        };
    }

    private static List<string> ParseGroupNames(string text)
    {
        return (text ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split(
                new[] { ',', '，', '、', '/', '|', '·', '・', ';', '；', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DeckDataService.NormalizeGroupName)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool MiracleEffectsEqual(
        MiracleLeaderEffect? first,
        MiracleLeaderEffect? second)
    {
        MiracleLeaderEffect left = DeckDataService.NormalizeMiracleLeaderEffect(first);
        MiracleLeaderEffect right = DeckDataService.NormalizeMiracleLeaderEffect(second);
        return left.IsEnabled == right.IsEnabled &&
               left.TargetGroups
                   .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
                   .SequenceEqual(
                       right.TargetGroups.OrderBy(group => group, StringComparer.OrdinalIgnoreCase),
                       StringComparer.OrdinalIgnoreCase) &&
               left.GrantedLetters
                   .OrderBy(letter => letter, StringComparer.Ordinal)
                   .SequenceEqual(
                       right.GrantedLetters.OrderBy(letter => letter, StringComparer.Ordinal),
                       StringComparer.Ordinal) &&
               string.Equals(left.Note, right.Note, StringComparison.Ordinal);
    }

    private static bool DeckGroupLetterEffectsEqual(
        DeckGroupLetterEffect? first,
        DeckGroupLetterEffect? second)
    {
        DeckGroupLetterEffect left = DeckDataService.NormalizeDeckGroupLetterEffect(first);
        DeckGroupLetterEffect right = DeckDataService.NormalizeDeckGroupLetterEffect(second);
        return left.IsEnabled == right.IsEnabled &&
               left.MinimumCount == right.MinimumCount &&
               left.TargetGroups
                   .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
                   .SequenceEqual(
                       right.TargetGroups.OrderBy(group => group, StringComparer.OrdinalIgnoreCase),
                       StringComparer.OrdinalIgnoreCase) &&
               left.GrantedLetters
                   .OrderBy(letter => letter, StringComparer.Ordinal)
                   .SequenceEqual(
                       right.GrantedLetters.OrderBy(letter => letter, StringComparer.Ordinal),
                       StringComparer.Ordinal) &&
               string.Equals(left.Note, right.Note, StringComparison.Ordinal);
    }

    private static List<string> ParseLetters(string text)
    {
        string normalized = (text ?? string.Empty)
            .Normalize(NormalizationForm.FormC);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            string value = rune.ToString();
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);

            if (Rune.IsWhiteSpace(rune) ||
                value is "," or "，" or "、" or "/" or "|" or "·" or "・" or ";" or "；")
            {
                continue;
            }

            if (category == UnicodeCategory.NonSpacingMark && result.Count > 0)
            {
                string combined = (result[^1] + value).Normalize(NormalizationForm.FormC);
                seen.Remove(result[^1]);
                result[^1] = combined;
                seen.Add(combined);
                continue;
            }

            string cell = KanaUtility.NormalizeCell(value);
            if (cell.Length > 0 && seen.Add(cell))
            {
                result.Add(cell);
            }
        }

        return result;
    }

    private ImageSource? GetCharacterThumbnail(CharacterEntry character)
    {
        string activeImageFileName = character.GetActiveImageFileName();
        if (string.IsNullOrWhiteSpace(activeImageFileName))
        {
            return null;
        }

        string activeFormKey = string.IsNullOrWhiteSpace(character.ActiveFormId)
            ? CharacterEntry.BaseFormId
            : character.ActiveFormId;
        string cacheKey = $"{character.Id}|{activeFormKey}|{activeImageFileName}";
        lock (_thumbnailCacheLock)
        {
            if (_thumbnailCache.TryGetValue(cacheKey, out ImageSource? cached))
            {
                return cached;
            }
        }

        // CharacterDisplayItem이 화면에 실제로 나타날 때 백그라운드에서 호출됩니다.
        // 2,500명 전체 PNG를 창이 열리기 전에 디코딩하지 않습니다.
        ImageSource? thumbnail = CharacterImageService.LoadBitmap(
            _dataDirectory,
            activeImageFileName,
            96);

        lock (_thumbnailCacheLock)
        {
            _thumbnailCache[cacheKey] = thumbnail;
        }
        return thumbnail;
    }

    private string GetCharacterListSummary(CharacterEntry character)
    {
        if (_listSummaryCache.TryGetValue(character.Id, out string? cached))
        {
            return cached;
        }

        string stateText = character.LetterStates.Count > 0
            ? $" · 상태 {character.LetterStates.Count}개"
            : string.Empty;
        string sameNameFormText = character.HasAlternateForms
            ? $" · 동일명 MS {character.AlternateForms.Count + 1}형태"
            : string.Empty;
        string restrictionText = string.IsNullOrWhiteSpace(character.DeckRestrictionGroupId)
            ? string.Empty
            : " · 이름 다른 MS";
        string groupText = string.IsNullOrWhiteSpace(character.GroupName)
            ? string.Empty
            : $" · 그룹 {character.GroupName}" +
              (GetEffectiveGroupNamesCached(character).Length > 1 ? "(포괄)" : string.Empty);
        string miracleText = DeckDataService.NormalizeMiracleLeaderEffect(
                character.MiracleLeaderEffect).IsConfigured
            ? " · 미라클 리더 효과"
            : string.Empty;
        string deckGroupText = DeckDataService.NormalizeDeckGroupLetterEffect(
                character.DeckGroupLetterEffect).IsConfigured
            ? " · 덱 인원 조건 문자"
            : string.Empty;

        string activeFormText = character.HasAlternateForms
            ? $" · 현재 MS {character.GetActiveFormName()}"
            : string.Empty;
        string summary = string.Join(" · ", character.GetAvailableLetters()) +
                         activeFormText + stateText + sameNameFormText + groupText + restrictionText + miracleText + deckGroupText;
        _listSummaryCache[character.Id] = summary;
        return summary;
    }

    private void InvalidateCharacterThumbnail(string characterId)
    {
        string prefix = characterId + "|";
        lock (_thumbnailCacheLock)
        {
            string[] keys = _thumbnailCache.Keys
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            foreach (string key in keys)
            {
                _thumbnailCache.Remove(key);
            }
        }

        _listSummaryCache.Remove(characterId);
        _searchCandidateCache.Remove(characterId);
        _effectiveGroupCache.Remove(characterId);
    }

    private void RebuildCharacterIndex()
    {
        _characterById.Clear();
        _relatedFormsCache.Clear();

        foreach (CharacterEntry character in _library)
        {
            if (!string.IsNullOrWhiteSpace(character.Id))
            {
                _characterById[character.Id] = character;
            }
        }

        // 이름이 다른 모드시프트를 검색할 때 캐릭터마다 전체 라이브러리를 다시 훑지 않습니다.
        // 제한 그룹별 멤버를 한 번 묶은 뒤 각 캐릭터에 연결 목록을 캐시합니다.
        foreach (IGrouping<string, CharacterEntry> group in _library
                     .Where(character => NormalizeRestrictionGroup(character.DeckRestrictionGroupId).Length > 0)
                     .GroupBy(
                         character => NormalizeRestrictionGroup(character.DeckRestrictionGroupId),
                         StringComparer.OrdinalIgnoreCase))
        {
            CharacterEntry[] members = group
                .OrderBy(character => CharacterCategories.GetSortOrder(character.Category))
                .ThenBy(character => character.Name, StringComparer.Ordinal)
                .ThenBy(character => character.Id, StringComparer.Ordinal)
                .ToArray();

            foreach (CharacterEntry character in members)
            {
                _relatedFormsCache[character.Id] = members
                    .Where(other => !string.Equals(other.Id, character.Id, StringComparison.Ordinal))
                    .ToArray();
            }
        }
    }

    private void MarkLibraryIndexesDirty(bool groupOptionsChanged = true)
    {
        RebuildCharacterIndex();
        _searchCandidateCache.Clear();
        _effectiveGroupCache.Clear();
        if (groupOptionsChanged)
        {
            _groupFilterOptionsDirty = true;
        }
    }

    private static int GetAttributeSortOrder(string? value)
    {
        string normalized = NormalizeAttribute(value);
        int index = Array.IndexOf(AttributeValues, normalized);
        return index >= 0 ? index : int.MaxValue;
    }

    private static int GetSpeciesSortOrder(string? value)
    {
        string normalized = NormalizeSpecies(value);
        int index = Array.IndexOf(SpeciesValues, normalized);
        return index >= 0 ? index : int.MaxValue;
    }

    private static List<string> ParseSearchAliases(string text)
    {
        string[] tokens = (text ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split(new[] { ',', '，', '、', '/', '／', '·', '・', '|', ';', '；', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return DeckDataService.NormalizeSearchAliases(tokens);
    }

    private static List<string> ParseSubAttributes(string text, string? mainAttribute)
    {
        string normalizedText = (text ?? string.Empty).Normalize(NormalizationForm.FormC);
        string[] tokens = normalizedText
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '，', '、', '/', '／', '·', '・', '|', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return DeckDataService.NormalizeAttributes(tokens, mainAttribute);
    }

    private static string NormalizeAttribute(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.EndsWith("属性", StringComparison.Ordinal))
        {
            normalized = normalized[..^2];
        }
        return AttributeValues.Contains(normalized, StringComparer.Ordinal) ? normalized : string.Empty;
    }

    private static string NormalizeSpecies(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.EndsWith("種族", StringComparison.Ordinal))
        {
            normalized = normalized[..^2];
        }
        return SpeciesValues.Contains(normalized, StringComparer.Ordinal) ? normalized : string.Empty;
    }

    private static string MetadataComboValue(string normalized)
        => string.IsNullOrWhiteSpace(normalized) ? "미입력" : normalized;

    private static string ReadMetadataCombo(ComboBox comboBox, bool isAttribute)
    {
        string raw = comboBox.SelectedItem as string ?? comboBox.Text ?? string.Empty;
        if (string.Equals(raw, "미입력", StringComparison.Ordinal))
        {
            return string.Empty;
        }
        return isAttribute ? NormalizeAttribute(raw) : NormalizeSpecies(raw);
    }

    private void SaveDeckEditorFilterStateSafely()
    {
        try
        {
            string characterId = _editingCharacterId
                ?? _lastSelectedCharacterId
                ?? string.Empty;
            string searchText = LibrarySearchTextBox.Text ?? string.Empty;
            string groupFilter = GroupFilterComboBox.SelectedItem as string ?? "전체 그룹";
            string categoryFilter = CategoryFilterComboBox.SelectedItem as string ?? "전체 등급";
            string attributeFilter = AttributeFilterComboBox.SelectedItem as string ?? "전체 속성";
            string speciesFilter = SpeciesFilterComboBox.SelectedItem as string ?? "전체 종족";
            string statusFilter = StatusFilterComboBox.SelectedItem as string ?? "전체 상태";
            string sortMode = SortComboBox.SelectedItem as string ?? "기본 정렬";
            bool favoritesOnly = FavoriteOnlyCheckBox.IsChecked == true;
            bool belovedOnly = BelovedOnlyCheckBox.IsChecked == true;

            UserSettingsService.Update(settings =>
            {
                settings.LastDeckEditorCharacterId = characterId;
                settings.LastDeckEditorSearchText = searchText;
                settings.LastDeckEditorGroupFilter = groupFilter;
                settings.LastDeckEditorCategoryFilter = categoryFilter;
                settings.LastDeckEditorAttributeFilter = attributeFilter;
                settings.LastDeckEditorSpeciesFilter = speciesFilter;
                settings.LastDeckEditorStatusFilter = statusFilter;
                settings.LastDeckEditorSortMode = sortMode;
                settings.LastDeckEditorFavoritesOnly = favoritesOnly;
                settings.LastDeckEditorBelovedOnly = belovedOnly;
            });
        }
        catch
        {
            // 검색 상태 복원 실패가 덱 편집 자체를 막지는 않게 합니다.
        }
    }

    private void FitWindowToCurrentWorkArea()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            IntPtr monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double workWidth = (info.Work.Right - info.Work.Left) / dpi.DpiScaleX;
            double workHeight = (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY;
            MaxWidth = Math.Max(MinWidth, workWidth - 16);
            MaxHeight = Math.Max(MinHeight, workHeight - 16);

            // 캐릭터가 많아진 뒤에는 목록 영역 높이가 작업 효율에 직접 영향을 줍니다.
            // 가능한 경우 모니터 작업 영역을 넉넉히 사용하되 작은 화면에서는 기존 최소 크기를 지킵니다.
            double preferredWidth = Math.Min(1360, workWidth - 24);
            double preferredHeight = Math.Min(960, workHeight - 24);
            Width = Math.Min(MaxWidth, Math.Max(MinWidth, preferredWidth));
            Height = Math.Min(MaxHeight, Math.Max(MinHeight, preferredHeight));
        }
        catch
        {
            // 모니터 작업 영역 계산 실패 시 XAML 기본 크기를 사용합니다.
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public RectInt Monitor;
        public RectInt Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectInt
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private CharacterEntry? FindCharacter(string? characterId)
        => !string.IsNullOrWhiteSpace(characterId) &&
           _characterById.TryGetValue(characterId, out CharacterEntry? character)
            ? character
            : null;

    private string? GetSelectedLibraryCharacterId()
        => CharacterLibraryListBox.SelectedItem is CharacterDisplayItem item
            ? item.Id
            : null;

    private int GetSelectedDeckIndexForCharacter(string characterId)
        => _deckIds.FindIndex(id => string.Equals(id, characterId, StringComparison.Ordinal));

    private void SelectCharacter(string characterId)
    {
        CharacterDisplayItem? visibleItem = CharacterLibraryListBox.Items
            .OfType<CharacterDisplayItem>()
            .FirstOrDefault(item => string.Equals(item.Id, characterId, StringComparison.Ordinal));
        if (visibleItem is null)
        {
            RefreshLibraryList(characterId);
        }
        else
        {
            _isRefreshingLists = true;
            CharacterLibraryListBox.SelectedItem = visibleItem;
            _isRefreshingLists = false;
            CharacterLibraryListBox.ScrollIntoView(visibleItem);
        }

        BeginEditing(characterId);
    }

    private void RemoveMissingDeckCharacters()
    {
        var validIds = _library
            .Select(character => character.Id)
            .ToHashSet(StringComparer.Ordinal);
        _deckIds.RemoveAll(id => !validIds.Contains(id));
    }

    private DeckPreset? FindPreset(string? presetId)
        => string.IsNullOrWhiteSpace(presetId)
            ? null
            : _presets.FirstOrDefault(preset =>
                string.Equals(preset.Id, presetId, StringComparison.Ordinal));

    private void RemoveMissingPresetCharacters()
    {
        var validIds = _library
            .Select(character => character.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (DeckPreset preset in _presets)
        {
            IEnumerable<string> validPresetIds = preset.CharacterIds
                .Where(validIds.Contains)
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumDeckSize);
            preset.CharacterIds = NormalizeDeckIdsForRestrictions(validPresetIds);
        }
    }

    private bool HasRestrictionConflict(
        string characterId,
        string? restrictionGroupId,
        out CharacterEntry? conflict)
    {
        conflict = null;
        string normalizedGroup = (restrictionGroupId ?? string.Empty).Trim();
        if (normalizedGroup.Length == 0)
        {
            return false;
        }

        foreach (string deckId in _deckIds)
        {
            if (string.Equals(deckId, characterId, StringComparison.Ordinal))
            {
                continue;
            }

            CharacterEntry? other = FindCharacter(deckId);
            if (other is not null &&
                string.Equals(
                    (other.DeckRestrictionGroupId ?? string.Empty).Trim(),
                    normalizedGroup,
                    StringComparison.OrdinalIgnoreCase))
            {
                conflict = other;
                return true;
            }
        }

        return false;
    }

    private List<string> NormalizeDeckIdsForRestrictions(IEnumerable<string> characterIds)
    {
        var result = new List<string>();
        var usedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string characterId in characterIds)
        {
            CharacterEntry? character = FindCharacter(characterId);
            if (character is null)
            {
                continue;
            }

            string group = (character.DeckRestrictionGroupId ?? string.Empty).Trim();
            if (group.Length > 0 && !usedGroups.Add(group))
            {
                continue;
            }

            result.Add(character.Id);
            if (result.Count >= MaximumDeckSize)
            {
                break;
            }
        }

        return result;
    }

    private bool ValidateCurrentDeckRestrictions(out string error)
    {
        error = string.Empty;
        var groups = new Dictionary<string, CharacterEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (string characterId in _deckIds)
        {
            CharacterEntry? character = FindCharacter(characterId);
            if (character is null)
            {
                continue;
            }

            string group = (character.DeckRestrictionGroupId ?? string.Empty).Trim();
            if (group.Length == 0)
            {
                continue;
            }

            if (groups.TryGetValue(group, out CharacterEntry? existing))
            {
                error = $"'{existing.Name}'과 '{character.Name}'은 같은 모드시프트 그룹이라 함께 저장할 수 없습니다.";
                return false;
            }

            groups[group] = character;
        }

        return true;
    }

    private void SetError(string message)
    {
        EditorStatusText.Text = message;
        EditorStatusText.Foreground = BrushFromHex("#FF9E9E");
    }

    private static SolidColorBrush BrushFromHex(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));

    private sealed record SearchTokenQuery(
        string Normalized,
        string Converted,
        string[] Cells);

    private static IReadOnlyList<string> GetAllCharacterAttributes(CharacterEntry character)
    {
        var values = new List<string>();

        void Add(string? value)
        {
            string normalized = NormalizeAttribute(value);
            if (normalized.Length > 0 && !values.Contains(normalized, StringComparer.Ordinal))
            {
                values.Add(normalized);
            }
        }

        Add(character.Attribute);
        foreach (string value in DeckDataService.NormalizeAttributes(character.SubAttributes, character.Attribute))
        {
            Add(value);
        }

        foreach (CharacterForm form in character.AlternateForms ?? new List<CharacterForm>())
        {
            Add(form.Attribute);
            foreach (string value in DeckDataService.NormalizeAttributes(form.SubAttributes, form.Attribute))
            {
                Add(value);
            }
        }

        return values;
    }

    private static IReadOnlyList<string> GetAllCharacterSpecies(CharacterEntry character)
    {
        var values = new List<string>();

        void Add(string? value)
        {
            string normalized = NormalizeSpecies(value);
            if (normalized.Length > 0 && !values.Contains(normalized, StringComparer.Ordinal))
            {
                values.Add(normalized);
            }
        }

        Add(character.Species);
        foreach (CharacterForm form in character.AlternateForms ?? new List<CharacterForm>())
        {
            Add(form.Species);
        }

        return values;
    }

    private static IReadOnlyList<string> GetActiveCharacterAttributes(CharacterEntry character)
    {
        if (string.Equals(character.ActiveFormId, CharacterEntry.AllFormsId, StringComparison.Ordinal))
        {
            return GetAllCharacterAttributes(character);
        }

        CharacterForm? form = character.FindForm(character.ActiveFormId);
        string main = NormalizeAttribute(form?.Attribute);
        if (main.Length == 0)
        {
            main = NormalizeAttribute(character.Attribute);
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

    private static string GetActiveCharacterSpecies(CharacterEntry character)
    {
        if (string.Equals(character.ActiveFormId, CharacterEntry.AllFormsId, StringComparison.Ordinal))
        {
            return string.Join("/", GetAllCharacterSpecies(character));
        }

        CharacterForm? form = character.FindForm(character.ActiveFormId);
        string species = NormalizeSpecies(form?.Species);
        return species.Length > 0 ? species : NormalizeSpecies(character.Species);
    }

    public sealed class PresetDisplayItem
    {
        public PresetDisplayItem(DeckPreset preset)
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

    public sealed class RelatedFormDisplayItem
    {
        public RelatedFormDisplayItem(CharacterEntry character)
        {
            Id = character.Id;
            Name = character.Name;
            DisplayText = $"[{CharacterCategories.Normalize(character.Category)}] {character.Name}  ·  {string.Join(" · ", character.GetAvailableLetters())}";
        }

        public string Id { get; }
        public string Name { get; }
        public string DisplayText { get; }

        public override string ToString() => DisplayText;
    }

    public sealed class AttributeDisplaySegment
    {
        public AttributeDisplaySegment(string text, Brush foreground)
        {
            Text = text;
            Foreground = foreground;
        }

        public string Text { get; }
        public Brush Foreground { get; }
    }

    public sealed class CharacterDisplayItem : INotifyPropertyChanged
    {
        private readonly Func<ImageSource?>? _thumbnailLoader;
        private readonly Lazy<string> _lettersText;
        private ImageSource? _thumbnail;
        private bool _thumbnailLoadStarted;
        private bool _thumbnailLoadCompleted;

        public CharacterDisplayItem(
            CharacterEntry character,
            Func<ImageSource?>? thumbnailLoader,
            Func<string> lettersTextLoader,
            int deckIndex)
        {
            Id = character.Id;
            Name = character.Name;
            Category = CharacterCategories.Normalize(character.Category);
            IReadOnlyList<string> activeAttributes = GetActiveCharacterAttributes(character);
            string activeSpecies = GetActiveCharacterSpecies(character);
            AttributeText = activeAttributes.Count == 0 ? "－" : string.Join("/", activeAttributes);
            AttributeSegments = BuildAttributeSegments(activeAttributes);
            SpeciesText = activeSpecies.Length == 0 ? "－" : activeSpecies;
            FavoriteMark = character.IsFavorite ? "★" : string.Empty;
            BelovedMark = character.IsBeloved ? "♥" : string.Empty;
            _thumbnailLoader = string.IsNullOrWhiteSpace(character.GetActiveImageFileName())
                ? null
                : thumbnailLoader;
            _thumbnailLoadCompleted = _thumbnailLoader is null;
            _lettersText = new Lazy<string>(lettersTextLoader);
            IsInDeck = deckIndex >= 0;
            DeckPositionText = deckIndex switch
            {
                0 => "★ 리더",
                >= 1 => $"덱 {deckIndex + 1}",
                _ => string.Empty
            };
            DeckBadgeVisibility = IsInDeck
                ? Visibility.Visible
                : Visibility.Collapsed;
            DeckActionText = IsInDeck ? "제거" : "추가";
            ModeShiftBadgeVisibility = character.HasAlternateForms
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public string Id { get; }
        public string Name { get; }
        public string Category { get; }
        public string AttributeText { get; }
        public IReadOnlyList<AttributeDisplaySegment> AttributeSegments { get; }
        public string SpeciesText { get; }
        public string FavoriteMark { get; }
        public string BelovedMark { get; }
        public string LettersText => _lettersText.Value;
        public ImageSource? Thumbnail
        {
            get
            {
                EnsureThumbnailLoading();
                return _thumbnail;
            }
        }
        public Visibility ThumbnailVisibility
        {
            get
            {
                EnsureThumbnailLoading();
                return _thumbnail is null ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        public Visibility PlaceholderVisibility
        {
            get
            {
                EnsureThumbnailLoading();
                return _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        public string PlaceholderText
        {
            get
            {
                EnsureThumbnailLoading();
                return _thumbnailLoadCompleted ? "이미지\n없음" : "…";
            }
        }
        public bool IsInDeck { get; }
        public string DeckPositionText { get; }
        public Visibility DeckBadgeVisibility { get; }
        public string DeckActionText { get; }
        public Visibility ModeShiftBadgeVisibility { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static IReadOnlyList<AttributeDisplaySegment> BuildAttributeSegments(
            IReadOnlyList<string> attributes)
        {
            var segments = new List<AttributeDisplaySegment>();
            if (attributes.Count == 0)
            {
                segments.Add(new AttributeDisplaySegment("－", BrushFromHex("#7E8B98")));
                return segments;
            }

            for (int index = 0; index < attributes.Count; index++)
            {
                if (index > 0)
                {
                    segments.Add(new AttributeDisplaySegment("/", BrushFromHex("#7E8B98")));
                }

                string attribute = attributes[index];
                string color = attribute switch
                {
                    "火" => "#FF6B6B",
                    "水" => "#63B3FF",
                    "木" => "#70D98B",
                    "光" => "#FFE27A",
                    "闇" => "#B995FF",
                    "天" => "#8FE9FF",
                    "冥" => "#C58A5A",
                    "虹" => "#FF9EDB",
                    _ => "#FFD08A"
                };
                segments.Add(new AttributeDisplaySegment(attribute, BrushFromHex(color)));
            }

            return segments;
        }

        private async void EnsureThumbnailLoading()
        {
            if (_thumbnailLoadStarted || _thumbnailLoadCompleted)
            {
                return;
            }

            _thumbnailLoadStarted = true;
            try
            {
                _thumbnail = await Task.Run(() => _thumbnailLoader?.Invoke());
            }
            catch
            {
                _thumbnail = null;
            }
            finally
            {
                _thumbnailLoadCompleted = true;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaceholderVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlaceholderText)));
            }
        }
    }

    public sealed class DeckDisplayItem
    {
        public DeckDisplayItem(
            int index,
            CharacterEntry character,
            ImageSource? thumbnail,
            string lettersText)
        {
            Index = index;
            Id = character.Id;
            SlotText = index == 0 ? "★ 리더" : $"{index + 1}";
            Name = character.Name;
            Thumbnail = thumbnail;
            ThumbnailVisibility = thumbnail is null
                ? Visibility.Collapsed
                : Visibility.Visible;
            PlaceholderVisibility = thumbnail is null
                ? Visibility.Visible
                : Visibility.Collapsed;
            IReadOnlyList<string> activeAttributes = GetActiveCharacterAttributes(character);
            string attributeText = string.Join("/", activeAttributes);
            string species = GetActiveCharacterSpecies(character);
            MetaBadgeText = string.Join(" · ", new[] { attributeText, species }.Where(value => value.Length > 0));
            bool hasConnectedModeShift = !string.IsNullOrWhiteSpace(character.DeckRestrictionGroupId);
            bool hasSameNameModeShift = character.HasAlternateForms;
            ModeShiftBadgeVisibility = hasConnectedModeShift || hasSameNameModeShift
                ? Visibility.Visible
                : Visibility.Collapsed;
            ModeShiftHintText = hasConnectedModeShift
                ? "2초 길게 눌러 연결형 모드시프트 전환"
                : hasSameNameModeShift
                    ? "2초 길게 눌러 동일명 모드시프트 전환"
                    : string.Empty;
            string formText = hasSameNameModeShift ? $"\n현재 형태: {character.GetActiveFormName()}" : string.Empty;
            ToolTipText = $"{character.Name}\n{lettersText}" +
                          (MetaBadgeText.Length > 0 ? $"\n{MetaBadgeText}" : string.Empty) +
                          formText +
                          (ModeShiftHintText.Length > 0 ? $"\n{ModeShiftHintText}" : string.Empty);
            BorderBrush = index == 0 ? BrushFromHexStatic("#FFD166") : BrushFromHexStatic("#3B657A");
            SlotForeground = index == 0 ? BrushFromHexStatic("#FFD166") : BrushFromHexStatic("#66D9EF");
        }

        public int Index { get; }
        public string Id { get; }
        public string SlotText { get; }
        public string Name { get; }
        public string MetaBadgeText { get; }
        public string ToolTipText { get; }
        public Brush BorderBrush { get; }
        public Brush SlotForeground { get; }
        public ImageSource? Thumbnail { get; }
        public Visibility ThumbnailVisibility { get; }
        public Visibility PlaceholderVisibility { get; }
        public Visibility ModeShiftBadgeVisibility { get; }
        public string ModeShiftHintText { get; }
        public string PlaceholderText => "이미지\n없음";

        private static Brush BrushFromHexStatic(string value)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
            brush.Freeze();
            return brush;
        }
    }
}
