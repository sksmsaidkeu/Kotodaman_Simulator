using KotodamanWordFinder.Themes;
﻿using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Services;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder;

public partial class CharacterBulkEditorWindow : Window
{
    private static string Unset => Loc.Unset;

    private static readonly string[] AttributeValues = { "火", "水", "木", "光", "闇", "天", "冥", "虹" };
    private static readonly string[] SpeciesValues = { "神", "魔", "英", "龍", "獣", "霊", "物", "妖" };

    private readonly ObservableCollection<CharacterBulkEditRow> _rows;
    private readonly ICollectionView _view;
    private readonly CharacterWebImportService _webImportService = new();
    private CancellationTokenSource? _gimmickFetchCancellation;
    private readonly DispatcherTimer _searchTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(220)
    };
    private SearchToken[] _searchTokens = Array.Empty<SearchToken>();

    // MatchesFilter는 ICollectionView가 행마다 호출하므로, Loc.Get 재조회 없이
    // RefreshFilterView에서 한 번만 계산해 둔 값을 읽습니다.
    private string _filterAllCategory = string.Empty;
    private string _filterAllAttribute = string.Empty;
    private string _filterAllSpecies = string.Empty;
    private string _filterUnset = string.Empty;
    private string _filterSelectedCategory = string.Empty;
    private string _filterSelectedAttribute = string.Empty;
    private string _filterSelectedSpecies = string.Empty;
    private bool _filterIncompleteOnly;

    public CharacterBulkEditorWindow(IReadOnlyList<CharacterEntry> characters, string dataDirectory)
    {
        InitializeComponent();
        _searchTimer.Tick += SearchTimer_Tick;
        Closed += (_, _) =>
        {
            _searchTimer.Stop();
            _gimmickFetchCancellation?.Cancel();
        };

        CategoryOptions = CharacterCategories.All.ToArray();
        AttributeOptions = new[] { Unset }.Concat(AttributeValues).ToArray();
        SpeciesOptions = new[] { Unset }.Concat(SpeciesValues).ToArray();

        CategoryFilterComboBox.ItemsSource = new[] { Loc.Get("Str.FilterAllCategory") }.Concat(CategoryOptions).ToArray();
        AttributeFilterComboBox.ItemsSource = new[] { Loc.Get("Str.FilterAllAttribute") }.Concat(AttributeValues).Concat(new[] { Unset }).ToArray();
        SpeciesFilterComboBox.ItemsSource = new[] { Loc.Get("Str.FilterAllSpecies") }.Concat(SpeciesValues).Concat(new[] { Unset }).ToArray();
        CategoryFilterComboBox.SelectedIndex = 0;
        AttributeFilterComboBox.SelectedIndex = 0;
        SpeciesFilterComboBox.SelectedIndex = 0;

        _rows = new ObservableCollection<CharacterBulkEditRow>(characters
            .OrderBy(character => CharacterCategories.GetSortOrder(character.Category))
            .ThenBy(character => character.Name, StringComparer.Ordinal)
            .ThenBy(character => character.Id, StringComparer.Ordinal)
            .Select(character => new CharacterBulkEditRow(
                character,
                string.IsNullOrWhiteSpace(character.GetActiveImageFileName())
                    ? null
                    : () => CharacterImageService.LoadBitmap(
                        dataDirectory,
                        character.GetActiveImageFileName(),
                        48))));

        CharacterDataGrid.ItemsSource = _rows;
        _view = CollectionViewSource.GetDefaultView(_rows);
        CaptureFilterState();
        _view.Filter = MatchesFilter;
        UpdateCountText();
    }

    public IReadOnlyList<string> CategoryOptions { get; }
    public IReadOnlyList<string> AttributeOptions { get; }
    public IReadOnlyList<string> SpeciesOptions { get; }

    public IReadOnlyList<string> ChangedCharacterIds { get; private set; } = Array.Empty<string>();

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        RefreshFilterView();
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _searchTimer.Stop();
        RefreshFilterView();
    }

    private void IncompleteOnlyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        RefreshFilterView();
    }

    private void RefreshFilterView()
    {
        if (!IsLoaded || _view is null)
        {
            return;
        }

        _searchTokens = BuildSearchTokens(SearchTextBox.Text);
        CaptureFilterState();
        _view.Refresh();
        UpdateCountText();
    }

    private void CaptureFilterState()
    {
        _filterAllCategory = Loc.Get("Str.FilterAllCategory");
        _filterAllAttribute = Loc.Get("Str.FilterAllAttribute");
        _filterAllSpecies = Loc.Get("Str.FilterAllSpecies");
        _filterUnset = Unset;
        _filterSelectedCategory = CategoryFilterComboBox.SelectedItem as string ?? _filterAllCategory;
        _filterSelectedAttribute = AttributeFilterComboBox.SelectedItem as string ?? _filterAllAttribute;
        _filterSelectedSpecies = SpeciesFilterComboBox.SelectedItem as string ?? _filterAllSpecies;
        _filterIncompleteOnly = IncompleteOnlyCheckBox.IsChecked == true;
    }

    private bool MatchesFilter(object item)
    {
        if (item is not CharacterBulkEditRow row)
        {
            return false;
        }

        if (!string.Equals(_filterSelectedCategory, _filterAllCategory, StringComparison.Ordinal) &&
            !string.Equals(row.Category, _filterSelectedCategory, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(_filterSelectedAttribute, _filterAllAttribute, StringComparison.Ordinal))
        {
            if (string.Equals(_filterSelectedAttribute, _filterUnset, StringComparison.Ordinal))
            {
                if (!string.Equals(row.Attribute, _filterUnset, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            else
            {
                bool hasAttribute = string.Equals(row.Attribute, _filterSelectedAttribute, StringComparison.Ordinal) ||
                                    ParseSubAttributes(row.SubAttributesText, row.Attribute)
                                        .Contains(_filterSelectedAttribute, StringComparer.Ordinal);
                if (!hasAttribute)
                {
                    return false;
                }
            }
        }

        if (!string.Equals(_filterSelectedSpecies, _filterAllSpecies, StringComparison.Ordinal) &&
            !string.Equals(row.Species, _filterSelectedSpecies, StringComparison.Ordinal))
        {
            return false;
        }

        if (_filterIncompleteOnly &&
            !string.Equals(row.Attribute, _filterUnset, StringComparison.Ordinal) &&
            !string.Equals(row.Species, _filterUnset, StringComparison.Ordinal))
        {
            return false;
        }

        if (_searchTokens.Length == 0)
        {
            return true;
        }

        string searchable = KanaUtility.ToSearchKey(row.BuildSearchText());
        foreach (SearchToken token in _searchTokens)
        {
            if (!searchable.Contains(token.Normalized, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(token.Converted, token.Normalized, StringComparison.Ordinal) ||
                 !searchable.Contains(token.Converted, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    private static SearchToken[] BuildSearchTokens(string? text)
    {
        return (text ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token =>
            {
                string normalized = KanaUtility.ToSearchKey(token);
                string converted = KanaUtility.ToSearchKey(
                    KanaUtility.ConvertHangulToKana(normalized));
                return new SearchToken(normalized, converted);
            })
            .ToArray();
    }

    private sealed record SearchToken(string Normalized, string Converted);

    private void UpdateCountText()
    {
        int visibleCount = _view?.Cast<object>().Count() ?? _rows.Count;
        int checkedCount = _rows.Count(row => row.IsChecked);
        CountText.Text = checkedCount > 0
            ? string.Format(Loc.Get("Str.CharBulk.CountStatusWithChecked"), visibleCount.ToString("N0"), _rows.Count.ToString("N0"), checkedCount.ToString("N0"))
            : string.Format(Loc.Get("Str.CharBulk.CountStatus"), visibleCount.ToString("N0"), _rows.Count.ToString("N0"));
    }

    private void CheckVisibleButton_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();
        foreach (CharacterBulkEditRow row in _view.Cast<CharacterBulkEditRow>())
        {
            row.IsChecked = true;
        }

        CharacterDataGrid.Items.Refresh();
        UpdateCountText();
        Controls.SetAlertBanner(StatusBanner, StatusText, string.Format(Loc.Get("Str.CharBulk.CheckedVisibleStatus"), _view.Cast<object>().Count().ToString("N0")), false, Theme.Success);
    }

    private void ClearChecksButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (CharacterBulkEditRow row in _rows)
        {
            row.IsChecked = false;
        }

        CharacterDataGrid.Items.Refresh();
        UpdateCountText();
        Controls.SetAlertBanner(StatusBanner, StatusText, Loc.Get("Str.CharBulk.UncheckedAllStatus"), false, Theme.TextSecondary);
    }

    private void ApplyBatchButton_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();
        CharacterBulkEditRow[] selected = _rows.Where(row => row.IsChecked).ToArray();
        if (selected.Length == 0)
        {
            SetError(Loc.Get("Str.CharBulk.SelectToBulkChange"));
            return;
        }

        string categoryInput = (BatchCategoryTextBox.Text ?? string.Empty).Trim();
        string attributeInput = (BatchAttributeTextBox.Text ?? string.Empty).Trim();
        string speciesInput = (BatchSpeciesTextBox.Text ?? string.Empty).Trim();
        string groupInput = (BatchGroupTextBox.Text ?? string.Empty).Trim();
        bool clearGroup = groupInput.Equals(Unset, StringComparison.OrdinalIgnoreCase) ||
                          groupInput.Equals("없음", StringComparison.OrdinalIgnoreCase) ||
                          groupInput == "-";
        string group = clearGroup
            ? string.Empty
            : DeckDataService.NormalizeGroupName(groupInput);
        bool anySetting = categoryInput.Length > 0 ||
                          attributeInput.Length > 0 ||
                          speciesInput.Length > 0 ||
                          groupInput.Length > 0;
        if (!anySetting)
        {
            SetError(Loc.Get("Str.CharBulk.EnterAtLeastOneBulkField"));
            return;
        }

        string category = string.Empty;
        if (categoryInput.Length > 0 && !TryNormalizeCategoryInput(categoryInput, out category))
        {
            SetError(string.Format(Loc.Get("Str.CharBulk.UnrecognizedCategory"), categoryInput));
            return;
        }

        string attribute = string.Empty;
        if (attributeInput.Length > 0 && !TryNormalizeAttributeInput(attributeInput, out attribute))
        {
            SetError(string.Format(Loc.Get("Str.CharBulk.UnrecognizedAttribute"), attributeInput));
            return;
        }

        string species = string.Empty;
        if (speciesInput.Length > 0 && !TryNormalizeSpeciesInput(speciesInput, out species))
        {
            SetError(string.Format(Loc.Get("Str.CharBulk.UnrecognizedSpecies"), speciesInput));
            return;
        }

        foreach (CharacterBulkEditRow row in selected)
        {
            if (categoryInput.Length > 0)
            {
                row.Category = category;
            }

            if (attributeInput.Length > 0)
            {
                row.Attribute = attribute.Length == 0 ? Unset : attribute;
                if (attribute.Length > 0)
                {
                    List<string> subAttributes = ParseSubAttributes(row.SubAttributesText, attribute);
                    row.SubAttributesText = string.Join(" ", subAttributes);
                }
            }

            if (speciesInput.Length > 0)
            {
                row.Species = species.Length == 0 ? Unset : species;
            }

            if (groupInput.Length > 0)
            {
                row.GroupName = group;
            }
        }

        CharacterDataGrid.Items.Refresh();
        _view.Refresh();
        UpdateCountText();
        Controls.SetAlertBanner(StatusBanner, StatusText, string.Format(Loc.Get("Str.CharBulk.BulkAppliedStatus"), selected.Length.ToString("N0")), false, Theme.Warn);
    }

    private async void FetchGimmickTagsButton_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();
        _gimmickFetchCancellation?.Cancel();
        _gimmickFetchCancellation?.Dispose();
        _gimmickFetchCancellation = new CancellationTokenSource();

        FetchGimmickTagsButton.IsEnabled = false;
        Controls.SetAlertBanner(StatusBanner, StatusText, Loc.Get("Str.CharBulk.GimmickFetching"), false, Theme.TextSecondary);

        try
        {
            GimmickTagCrawlResult result = await _webImportService.CrawlGimmickTagsAsync(
                _rows.Select(row => row.Source).ToArray(),
                _gimmickFetchCancellation.Token);

            int filledCount = 0;
            foreach (CharacterBulkEditRow row in _rows)
            {
                if (!result.TagsByCharacterId.TryGetValue(row.Id, out (List<string> Gimmicks, List<string> Statuses) tags))
                {
                    continue;
                }

                row.GimmickCountersText = string.Join(" · ", tags.Gimmicks);
                row.StatusResistancesText = string.Join(" · ", tags.Statuses);
                filledCount++;
            }

            CharacterDataGrid.Items.Refresh();
            Controls.SetAlertBanner(
                StatusBanner,
                StatusText,
                string.Format(
                    Loc.Get("Str.CharBulk.GimmickFetchDone"),
                    result.MatchedCount.ToString("N0"),
                    result.TotalRows.ToString("N0"),
                    filledCount.ToString("N0")),
                false,
                Theme.Success);
        }
        catch (OperationCanceledException)
        {
            // 창을 닫거나 다시 눌러 취소한 경우 조용히 무시합니다.
        }
        catch (Exception exception)
        {
            SetError(string.Format(Loc.Get("Str.CharBulk.GimmickFetchFailed"), exception.Message));
        }
        finally
        {
            FetchGimmickTagsButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdit();

        foreach (CharacterBulkEditRow row in _rows)
        {
            string name = (row.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                SetError(string.Format(Loc.Get("Str.CharBulk.NameEmptyForId"), row.Id));
                SelectRow(row);
                return;
            }

            if (!TryNormalizeCategoryInput(row.Category, out _))
            {
                SetError(string.Format(Loc.Get("Str.CharBulk.RowUnrecognizedCategory"), name, row.Category));
                SelectRow(row);
                return;
            }

            if (!TryNormalizeAttributeInput(row.Attribute, out _))
            {
                SetError(string.Format(Loc.Get("Str.CharBulk.RowUnrecognizedAttribute"), name, row.Attribute));
                SelectRow(row);
                return;
            }

            if (!TryNormalizeSpeciesInput(row.Species, out _))
            {
                SetError(string.Format(Loc.Get("Str.CharBulk.RowUnrecognizedSpecies"), name, row.Species));
                SelectRow(row);
                return;
            }

            List<string> letters = ParseLetters(row.LettersText);
            bool hasOtherLetters = row.Source.OneTimeLetters.Count > 0 ||
                                   row.Source.AlternateForms.Any(form => form.Letters.Count > 0) ||
                                   row.Source.LetterStates.Any(state => state.Letters.Count > 0);
            if (letters.Count == 0 && !hasOtherLetters)
            {
                SetError(string.Format(Loc.Get("Str.CharBulk.RowLettersEmpty"), name));
                SelectRow(row);
                return;
            }
        }

        var changedIds = new List<string>();
        foreach (CharacterBulkEditRow row in _rows)
        {
            if (row.ApplyToSource())
            {
                changedIds.Add(row.Id);
            }
        }

        ChangedCharacterIds = changedIds;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void SelectRow(CharacterBulkEditRow row)
    {
        IncompleteOnlyCheckBox.IsChecked = false;
        CategoryFilterComboBox.SelectedIndex = 0;
        AttributeFilterComboBox.SelectedIndex = 0;
        SpeciesFilterComboBox.SelectedIndex = 0;
        SearchTextBox.Clear();
        _view.Refresh();
        CharacterDataGrid.SelectedItem = row;
        CharacterDataGrid.ScrollIntoView(row);
    }

    private void CommitGridEdit()
    {
        CharacterDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        CharacterDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void SetError(string message)
        => Controls.SetAlertBanner(StatusBanner, StatusText, message, true);

    private static List<string> ParseLetters(string text)
    {
        string normalized = (text ?? string.Empty).Normalize(NormalizationForm.FormC);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            string value = rune.ToString();
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsWhiteSpace(rune) || value is "," or "，" or "、" or "/" or "|" or "·" or "・" or ";" or "；")
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

    private static List<string> ParseSubAttributes(string text, string? mainAttribute)
    {
        string main = NormalizeMetadataValue(mainAttribute);
        string[] tokens = (text ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '，', '、', '/', '／', '·', '・', '|', ';', '；' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return DeckDataService.NormalizeAttributes(tokens, main);
    }

    private static List<string> ParseSearchAliases(string text)
    {
        string[] tokens = (text ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split(new[] { ',', '，', '、', '/', '／', '·', '・', '|', ';', '；', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return DeckDataService.NormalizeSearchAliases(tokens);
    }

    private static List<string> ParseFreeTextList(string text)
    {
        string[] tokens = (text ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split(new[] { ',', '，', '、', '/', '／', '·', '・', '|', ';', '；', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string token in tokens)
        {
            if (seen.Add(token))
            {
                result.Add(token);
            }
        }

        return result;
    }

    private static List<string> ParseGroups(string text)
    {
        string[] tokens = (text ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Split(new[] { ',', '，', '、', '/', '／', '·', '・', '|', ';', '；', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return DeckDataService.NormalizeGroupNames(tokens);
    }

    private static bool TryNormalizeCategoryInput(string? value, out string normalized)
    {
        string input = (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        // 등급 자체(스페셜/레전드 등)는 게임 데이터라 번역하지 않지만, 오류 힌트(Str.CharBulk.UnrecognizedCategory)에는
        // en/ja 예시 단어를 보여주므로 그 단어들도 정규 한국어 값으로 인식해야 한다.
        input = input.ToLowerInvariant() switch
        {
            "special" => CharacterCategories.Special,
            "legend" => CharacterCategories.Legend,
            "grand" => CharacterCategories.Grand,
            "dream" => CharacterCategories.Dream,
            "miracle" => CharacterCategories.Miracle,
            "original" => CharacterCategories.Original,
            "collab" or "collaboration" => CharacterCategories.Collaboration,
            "other" => CharacterCategories.Other,
            "スペシャル" => CharacterCategories.Special,
            "レジェンド" => CharacterCategories.Legend,
            "グランド" => CharacterCategories.Grand,
            "ドリーム" => CharacterCategories.Dream,
            "ミラクル" => CharacterCategories.Miracle,
            "オリジナル" => CharacterCategories.Original,
            "コラボ" => CharacterCategories.Collaboration,
            "その他" => CharacterCategories.Other,
            _ => input
        };
        normalized = CharacterCategories.Normalize(input);
        return CharacterCategories.All.Contains(input, StringComparer.Ordinal);
    }

    private static bool TryNormalizeAttributeInput(string? value, out string normalized)
    {
        string input = (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (input.Length == 0 || string.Equals(input, Unset, StringComparison.Ordinal))
        {
            normalized = string.Empty;
            return true;
        }

        input = input.Replace("속성", string.Empty, StringComparison.Ordinal)
                     .Replace("属性", string.Empty, StringComparison.Ordinal)
                     .Trim();
        input = input switch
        {
            "화" or "불" => "火",
            "수" or "물" => "水",
            "목" or "나무" => "木",
            "광" or "빛" => "光",
            "암" or "어둠" => "闇",
            "천" => "天",
            "명" => "冥",
            "홍" or "무지개" => "虹",
            _ => input
        };

        normalized = DeckDataService.NormalizeAttribute(input);
        return normalized.Length > 0;
    }

    private static bool TryNormalizeSpeciesInput(string? value, out string normalized)
    {
        string input = (value ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (input.Length == 0 || string.Equals(input, Unset, StringComparison.Ordinal))
        {
            normalized = string.Empty;
            return true;
        }

        input = input.Replace("종족", string.Empty, StringComparison.Ordinal)
                     .Replace("種族", string.Empty, StringComparison.Ordinal)
                     .Trim();
        input = input switch
        {
            "신" => "神",
            "마" or "악마" or "悪魔" => "魔",
            "영" or "영웅" or "英雄" => "英",
            "용" or "룡" => "龍",
            "수" or "짐승" => "獣",
            "령" or "영혼" => "霊",
            "물" or "물체" => "物",
            "요" or "요괴" => "妖",
            _ => input
        };

        normalized = DeckDataService.NormalizeSpecies(input);
        return normalized.Length > 0;
    }

    private static string NormalizeMetadataValue(string? value)
        => string.Equals(value, Unset, StringComparison.Ordinal) ? string.Empty : (value ?? string.Empty).Trim();

    public sealed class CharacterBulkEditRow : INotifyPropertyChanged
    {
        private readonly Func<ImageSource?>? _thumbnailLoader;
        private ImageSource? _thumbnail;
        private bool _thumbnailLoadStarted;
        private bool _thumbnailLoadCompleted;

        public CharacterBulkEditRow(CharacterEntry source, Func<ImageSource?>? thumbnailLoader)
        {
            Source = source;
            _thumbnailLoader = thumbnailLoader;
            _thumbnailLoadCompleted = _thumbnailLoader is null;
            Id = source.Id;
            Name = source.Name;
            SearchAliasesText = string.Join(" · ", DeckDataService.NormalizeSearchAliases(source.SearchAliases));
            AutomaticAliasesText = string.Join(" ", CharacterSearchAliasUtility.BuildAutomaticAliases(source.Name));
            LettersText = string.Join(" ", source.Letters ?? new List<string>());
            Category = CharacterCategories.Normalize(source.Category);
            Attribute = string.IsNullOrWhiteSpace(source.Attribute) ? Unset : DeckDataService.NormalizeAttribute(source.Attribute);
            if (string.IsNullOrWhiteSpace(Attribute))
            {
                Attribute = Unset;
            }
            SubAttributesText = string.Join(" ", DeckDataService.NormalizeAttributes(source.SubAttributes, source.Attribute));
            Species = string.IsNullOrWhiteSpace(source.Species) ? Unset : DeckDataService.NormalizeSpecies(source.Species);
            if (string.IsNullOrWhiteSpace(Species))
            {
                Species = Unset;
            }
            GroupName = DeckDataService.NormalizeGroupName(source.GroupName);
            IncludedGroupsText = string.Join(" · ", DeckDataService.NormalizeGroupNames(source.IncludedGroups));
            GimmickCountersText = string.Join(" · ", source.GimmickCounters ?? new List<string>());
            StatusResistancesText = string.Join(" · ", source.StatusResistances ?? new List<string>());
            IsFavorite = source.IsFavorite;
            IsBeloved = source.IsBeloved;
        }

        public CharacterEntry Source { get; }
        public string Id { get; }
        public bool IsChecked { get; set; }
        public string Name { get; set; }
        public string SearchAliasesText { get; set; }
        public string AutomaticAliasesText { get; }
        public string LettersText { get; set; }
        public string Category { get; set; }
        public string Attribute { get; set; }
        public string SubAttributesText { get; set; }
        public string Species { get; set; }
        public string GroupName { get; set; }
        public string IncludedGroupsText { get; set; }
        public string GimmickCountersText { get; set; }
        public string StatusResistancesText { get; set; }
        public bool IsFavorite { get; set; }
        public bool IsBeloved { get; set; }

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
                return _thumbnailLoadCompleted ? Loc.Get("Str.CharBulk.ThumbnailNone") : "…";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string BuildSearchText()
            => string.Join(" ", new[]
            {
                Name,
                SearchAliasesText,
                AutomaticAliasesText,
                LettersText,
                Category,
                Attribute,
                SubAttributesText,
                Species,
                GroupName,
                IncludedGroupsText,
                GimmickCountersText,
                StatusResistancesText,
                Id
            }).Normalize(NormalizationForm.FormC);

        public bool ApplyToSource()
        {
            string name = (Name ?? string.Empty).Trim();
            List<string> searchAliases = ParseSearchAliases(SearchAliasesText);
            List<string> letters = ParseLetters(LettersText);
            _ = TryNormalizeCategoryInput(Category, out string category);
            _ = TryNormalizeAttributeInput(Attribute, out string attribute);
            List<string> subAttributes = ParseSubAttributes(SubAttributesText, attribute);
            _ = TryNormalizeSpeciesInput(Species, out string species);
            string groupName = DeckDataService.NormalizeGroupName(GroupName);
            List<string> includedGroups = ParseGroups(IncludedGroupsText)
                .Where(group => !string.Equals(group, groupName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<string> gimmickCounters = ParseFreeTextList(GimmickCountersText);
            List<string> statusResistances = ParseFreeTextList(StatusResistancesText);

            bool changed = !string.Equals(Source.Name, name, StringComparison.Ordinal) ||
                           !DeckDataService.NormalizeSearchAliases(Source.SearchAliases)
                               .SequenceEqual(searchAliases, StringComparer.OrdinalIgnoreCase) ||
                           !Source.Letters.SequenceEqual(letters, StringComparer.Ordinal) ||
                           !string.Equals(CharacterCategories.Normalize(Source.Category), category, StringComparison.Ordinal) ||
                           !string.Equals(DeckDataService.NormalizeAttribute(Source.Attribute), attribute, StringComparison.Ordinal) ||
                           !DeckDataService.NormalizeAttributes(Source.SubAttributes, Source.Attribute)
                               .SequenceEqual(subAttributes, StringComparer.Ordinal) ||
                           !string.Equals(DeckDataService.NormalizeSpecies(Source.Species), species, StringComparison.Ordinal) ||
                           !string.Equals(DeckDataService.NormalizeGroupName(Source.GroupName), groupName, StringComparison.Ordinal) ||
                           !DeckDataService.NormalizeGroupNames(Source.IncludedGroups)
                               .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
                               .SequenceEqual(includedGroups.OrderBy(group => group, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase) ||
                           !(Source.GimmickCounters ?? new List<string>()).SequenceEqual(gimmickCounters, StringComparer.Ordinal) ||
                           !(Source.StatusResistances ?? new List<string>()).SequenceEqual(statusResistances, StringComparer.Ordinal) ||
                           Source.IsFavorite != IsFavorite ||
                           Source.IsBeloved != IsBeloved;

            if (!changed)
            {
                return false;
            }

            Source.Name = name;
            Source.SearchAliases = searchAliases;
            Source.Letters = letters;
            Source.Category = category;
            Source.Attribute = attribute;
            Source.SubAttributes = subAttributes;
            Source.Species = species;
            Source.GroupName = groupName;
            Source.IncludedGroups = includedGroups;
            Source.GimmickCounters = gimmickCounters;
            Source.StatusResistances = statusResistances;
            Source.IsFavorite = IsFavorite;
            Source.IsBeloved = IsBeloved;
            return true;
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
}
