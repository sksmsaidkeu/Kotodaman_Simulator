using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Services;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder;

public partial class CharacterWebImportWindow : Window
{
    private readonly CharacterWebImportService _service = new();
    private readonly IReadOnlyList<string> _knownGroups;
    private readonly IReadOnlyDictionary<string, string[]> _knownGroupRelations;
    private readonly HashSet<string> _existingCharacterKeys;
    private readonly HashSet<string> _existingCharacterNames;
    private readonly List<CharacterImportReviewItem> _batchReviewItems = new();
    private readonly Dictionary<string, string> _discoveredGroupHints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GameWithCharacterLink> _discoveredLinks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _originalOnlyCandidateUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _lastFailedUrls = new();
    private CancellationTokenSource? _fetchCancellation;
    private CancellationTokenSource? _batchCancellation;
    private CharacterImportData? _fetchedData;
    private string _downloadedImagePath = string.Empty;

    public CharacterWebImportWindow(
        IEnumerable<string> knownGroups,
        IReadOnlyDictionary<string, string[]>? knownGroupRelations = null,
        IEnumerable<CharacterEntry>? existingCharacters = null)
    {
        InitializeComponent();
        Loaded += CharacterWebImportWindow_Loaded;
        _knownGroups = (knownGroups ?? Array.Empty<string>())
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.Ordinal)
            .ToArray();
        _knownGroupRelations = knownGroupRelations is null
            ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            : knownGroupRelations.ToDictionary(
                item => item.Key,
                item => item.Value ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        _existingCharacterKeys = (existingCharacters ?? Array.Empty<CharacterEntry>())
            .Where(character => !string.IsNullOrWhiteSpace(character.Name))
            .Select(character => BuildCharacterKey(character.Name, character.Letters))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _existingCharacterNames = (existingCharacters ?? Array.Empty<CharacterEntry>())
            .Where(character => !string.IsNullOrWhiteSpace(character.Name))
            .Select(character => NormalizeCharacterNameKey(character.Name))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        CategoryComboBox.ItemsSource = CharacterCategories.All;
        CategoryComboBox.SelectedItem = CharacterCategories.Other;
        AttributeComboBox.ItemsSource = new[] { "미입력", "火", "水", "木", "光", "闇", "天", "冥", "虹" };
        AttributeComboBox.SelectedItem = "미입력";
        SpeciesComboBox.ItemsSource = new[] { "미입력", "神", "魔", "英", "龍", "獣", "霊", "物", "妖" };
        SpeciesComboBox.SelectedItem = "미입력";
        BatchCategoryComboBox.ItemsSource = CharacterCategories.All;
        BatchCategoryComboBox.SelectedItem = CharacterCategories.Other;
        GroupComboBox.ItemsSource = _knownGroups;

        try
        {
            string clipboard = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : string.Empty;
            if (Uri.TryCreate(clipboard, UriKind.Absolute, out Uri? uri) &&
                uri.Host.EndsWith("gamewith.jp", StringComparison.OrdinalIgnoreCase))
            {
                SourceUrlTextBox.Text = clipboard;
            }
        }
        catch
        {
            // 클립보드 접근 실패는 무시합니다.
        }
    }

    private void CharacterWebImportWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 고해상도 배율/작은 화면에서도 창이 작업 영역 밖으로 잘리지 않게 맞춥니다.
        double availableHeight = Math.Max(560, SystemParameters.WorkArea.Height - 24);
        double availableWidth = Math.Max(760, SystemParameters.WorkArea.Width - 24);
        MaxHeight = availableHeight;
        MaxWidth = availableWidth;
        Height = Math.Min(820, availableHeight);
        Width = Math.Min(1080, availableWidth);
    }

    public CharacterImportPreview? ImportedPreview { get; private set; }

    public IReadOnlyList<CharacterImportPreview> ImportedPreviews { get; private set; }
        = Array.Empty<CharacterImportPreview>();

    private async void FetchButton_Click(object sender, RoutedEventArgs e)
    {
        string url = SourceUrlTextBox.Text.Trim();
        if (url.Length == 0)
        {
            SetStatus("캐릭터 페이지 주소를 입력하세요.", isError: true);
            SourceUrlTextBox.Focus();
            return;
        }

        _fetchCancellation?.Cancel();
        _fetchCancellation?.Dispose();
        _fetchCancellation = new CancellationTokenSource();

        DeleteTemporaryFile(_downloadedImagePath);
        _downloadedImagePath = string.Empty;
        CleanupBatchTemporaryFiles();
        _batchReviewItems.Clear();
        BatchReviewDataGrid.ItemsSource = null;
        ImportedPreviews = Array.Empty<CharacterImportPreview>();
        BatchApplyButton.IsEnabled = false;
        _fetchedData = null;
        SetBusy(true);
        SetStatus("웹 페이지를 읽고 있습니다…", isError: false);
        WarningText.Text = string.Empty;
        CharacterImagePreview.Source = null;
        ImagePlaceholderText.Visibility = Visibility.Visible;
        _downloadedImagePath = string.Empty;

        try
        {
            CharacterImportData data = await _service.ImportAsync(
                url,
                enrichFromDatabase: false,
                databaseOverrideUrl: null,
                cancellationToken: _fetchCancellation.Token,
                knownGroups: _knownGroups,
                knownGroupRelations: _knownGroupRelations);
            _fetchedData = data;
            PopulatePreview(data);

            if (data.ImageUrl.Length > 0)
            {
                SetStatus("캐릭터 정보 확인 완료 · 이미지를 불러오는 중…", isError: false);
                try
                {
                    _downloadedImagePath = await DownloadSupportedImageAsync(
                        data.ImageUrl,
                        _fetchCancellation.Token);
                    if (_downloadedImagePath.Length > 0)
                    {
                        CharacterImagePreview.Source = CharacterImageService.LoadBitmapFromPath(
                            _downloadedImagePath,
                            260);
                        if (CharacterImagePreview.Source is null)
                        {
                            AppendWarning("다운로드한 이미지를 해석하지 못했습니다. 이미지는 직접 선택하세요.");
                            DeleteTemporaryFile(_downloadedImagePath);
                            _downloadedImagePath = string.Empty;
                            UseImageCheckBox.IsChecked = false;
                        }

                        ImagePlaceholderText.Visibility = CharacterImagePreview.Source is null
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                    else
                    {
                        AppendWarning("웹 이미지 형식을 현재 앱에서 지원하지 않습니다. 이미지는 직접 선택하세요.");
                        UseImageCheckBox.IsChecked = false;
                    }
                }
                catch (Exception imageException)
                {
                    AppendWarning($"이미지 자동 다운로드 실패: {imageException.Message}");
                    UseImageCheckBox.IsChecked = false;
                }
            }

            ApplyButton.IsEnabled = data.Name.Length > 0;
            SetStatus("가져온 값을 확인하고 필요한 부분을 수정한 뒤 상세 편집으로 넘기세요.", isError: false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("가져오기를 취소했습니다.", isError: false);
        }
        catch (Exception exception)
        {
            _fetchedData = null;
            ApplyButton.IsEnabled = false;
            SetStatus($"정보를 가져오지 못했습니다: {exception.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulatePreview(CharacterImportData data)
    {
        IReadOnlyList<string> groupCandidates = data.GroupCandidates ?? new List<string>();
        IReadOnlyList<string> includedGroups = data.IncludedGroups ?? new List<string>();
        IReadOnlyList<string> letters = data.Letters ?? new List<string>();
        IReadOnlyList<string> notes = data.Notes ?? new List<string>();

        CharacterNameTextBox.Text = data.Name;
        CategoryComboBox.SelectedItem = CharacterCategories.Normalize(data.Category);
        string[] groupOptions = _knownGroups
            .Concat(groupCandidates)
            .Append(data.GroupName)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group, StringComparer.Ordinal)
            .ToArray();
        GroupComboBox.ItemsSource = groupOptions;
        GroupComboBox.Text = data.GroupName;
        LettersTextBox.Text = string.Join(" ", letters);
        string normalizedAttribute = DeckDataService.NormalizeAttribute(data.Attribute);
        AttributeComboBox.SelectedItem = normalizedAttribute.Length > 0 ? normalizedAttribute : "미입력";
        SubAttributesTextBox.Text = string.Join(" / ", DeckDataService.NormalizeAttributes(data.SubAttributes, normalizedAttribute));
        string normalizedSpecies = DeckDataService.NormalizeSpecies(data.Species);
        SpeciesComboBox.SelectedItem = normalizedSpecies.Length > 0 ? normalizedSpecies : "미입력";
        IncludedGroupsTextBox.Text = string.Join(" · ", includedGroups);
        GroupCandidateText.Text = groupCandidates.Count > 0
            ? $"인식 후보: {string.Join(" · ", groupCandidates)}"
            : "인식 후보 없음 · 필요하면 직접 선택";
        ImageUrlText.Text = data.ImageUrl.Length > 0 ? data.ImageUrl : "인식된 이미지 주소 없음";
        UseImageCheckBox.IsChecked = data.ImageUrl.Length > 0;

        string subAttributeText = data.SubAttributes.Count > 0 ? $" / {string.Join("/", data.SubAttributes)}" : string.Empty;
        string metadataText = $"속성: {(data.Attribute.Length > 0 ? data.Attribute + subAttributeText : "미인식")} · 종족: {(data.Species.Length > 0 ? data.Species : "미인식")}";
        SourceSummaryText.Text = $"{metadataText}\n출처: {data.SourceSite}\n{data.SourceUrl}";
        WarningText.Text = string.Join("\n", notes.Select(note => $"• {note}"));
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_fetchedData is null)
        {
            SetStatus("먼저 웹 정보를 가져오세요.", isError: true);
            return;
        }

        string name = CharacterNameTextBox.Text.Trim();
        List<string> letters = ParseLetters(LettersTextBox.Text);
        if (name.Length == 0)
        {
            SetStatus("캐릭터 이름을 입력하세요.", isError: true);
            CharacterNameTextBox.Focus();
            return;
        }
        if (letters.Count == 0)
        {
            SetStatus("사용 가능 문자를 하나 이상 확인하세요.", isError: true);
            LettersTextBox.Focus();
            return;
        }

        string imagePath = UseImageCheckBox.IsChecked == true
            ? _downloadedImagePath
            : string.Empty;
        if (imagePath.Length == 0)
        {
            DeleteTemporaryFile(_downloadedImagePath);
            _downloadedImagePath = string.Empty;
        }

        string attribute = ReadMetadataCombo(AttributeComboBox, isAttribute: true);
        string species = ReadMetadataCombo(SpeciesComboBox, isAttribute: false);

        ImportedPreview = new CharacterImportPreview
        {
            Name = name,
            Category = CharacterCategories.Normalize(CategoryComboBox.SelectedItem as string),
            Attribute = attribute,
            SubAttributes = ParseSubAttributes(SubAttributesTextBox.Text, attribute),
            Species = species,
            GroupName = GroupComboBox.Text.Trim(),
            IncludedGroups = ParseGroups(IncludedGroupsTextBox.Text),
            Letters = letters,
            ImageUrl = _fetchedData.ImageUrl,
            SourceUrl = _fetchedData.SourceUrl,
            SourceSite = _fetchedData.SourceSite,
            MatchedDatabaseUrl = _fetchedData.MatchedDatabaseUrl,
            DownloadedImagePath = imagePath,
            Notes = (_fetchedData.Notes ?? new List<string>()).ToList()
        };
        ImportedPreviews = Array.Empty<CharacterImportPreview>();

        DialogResult = true;
        Close();
    }

    private async void RatedDiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        var ratings = new List<string>();
        if (RatedSSSCheckBox.IsChecked == true) ratings.Add("SSS");
        if (RatedSSCheckBox.IsChecked == true) ratings.Add("SS");
        if (RatedSCheckBox.IsChecked == true) ratings.Add("S");
        if (ratings.Count == 0)
        {
            SetRatedSearchStatus("검색할 평가를 하나 이상 선택하세요.", isError: true);
            return;
        }

        GameWithRatingMatchMode matchMode = ReadRatingMatchMode();
        bool originalOnly = RatedOriginalOnlyCheckBox.IsChecked == true;
        bool includeRecentSixStarA = RatedRecentSixStarACheckBox.IsChecked == true && originalOnly;
        bool excludeRegistered = RatedExcludeRegisteredCheckBox.IsChecked == true;
        const string allCharactersUrl = "https://gamewith.jp/kotodaman/article/show/99665";

        _batchCancellation?.Cancel();
        _batchCancellation?.Dispose();
        _batchCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _batchCancellation.Token;
        SetBatchBusy(true);
        BatchLogTextBox.Clear();
        SetRatedSearchStatus($"GameWith 전 캐릭터 표에서 {string.Join("·", ratings)} 조건을 찾고 있습니다…", isError: false);
        SetBatchSummary("전 캐릭터 평가표를 분석하고 있습니다…", isError: false);

        try
        {
            IReadOnlyList<GameWithCharacterLink> found = await _service.DiscoverGameWithRatedCharacterLinksAsync(
                allCharactersUrl,
                ratings,
                matchMode,
                originalOnly,
                includeRecentSixStarA,
                cancellationToken);

            GameWithCharacterLink[] selected = found
                .Where(link => !excludeRegistered || !_existingCharacterNames.Contains(NormalizeCharacterNameKey(link.NameHint)))
                .ToArray();
            int excludedRegisteredCount = found.Count - selected.Length;

            _discoveredLinks.Clear();
            _discoveredGroupHints.Clear();
            _originalOnlyCandidateUrls.Clear();
            foreach (GameWithCharacterLink link in selected)
            {
                _discoveredLinks[link.Url] = link;
                if (!string.IsNullOrWhiteSpace(link.GroupHint))
                {
                    _discoveredGroupHints[link.Url] = link.GroupHint;
                }
                if (originalOnly)
                {
                    _originalOnlyCandidateUrls.Add(link.Url);
                }
            }

            BatchUrlTextBox.Text = string.Join(Environment.NewLine, selected.Select(link => link.Url));
            if (originalOnly)
            {
                BatchCategoryComboBox.SelectedItem = CharacterCategories.Original;
            }

            string modeText = matchMode switch
            {
                GameWithRatingMatchMode.SubOnly => "서브 평가",
                GameWithRatingMatchMode.LeaderOnly => "리더 평가",
                GameWithRatingMatchMode.Both => "서브·리더 모두",
                _ => "서브/리더 중 하나"
            };
            string originalText = originalOnly ? " · 오리지널 후보" : string.Empty;
            int recentACount = selected.Count(link => link.RequiresRecentSixStarValidation);
            string recentAText = includeRecentSixStarA ? $" · 최신 6성 A 재확인 {recentACount}개" : string.Empty;
            string existingText = excludeRegistered ? $" · 등록 이름 제외 {excludedRegisteredCount}개" : string.Empty;
            SetRatedSearchStatus(
                $"찾기 완료 · {modeText} {string.Join("·", ratings)}{originalText}{recentAText} · 링크 {selected.Length}개{existingText}",
                isError: selected.Length == 0);
            SetBatchSummary(
                selected.Length > 0
                    ? $"조건에 맞는 링크 {selected.Length}개를 입력했습니다. '연속 가져오기 시작'을 누르면 개별 페이지에서 오리지널 여부를 한 번 더 검사합니다."
                    : "조건에 맞는 미등록 캐릭터가 없습니다.",
                isError: selected.Length == 0);

            foreach (GameWithCharacterLink link in selected.Take(80))
            {
                string originalState = link.IsCollaboration switch
                {
                    false => "오리지널 확인",
                    true => "콜라보 메타 후보 · 개별 페이지 재확인",
                    _ => "개별 페이지 재확인"
                };
                string validationText = link.RequiresRecentSixStarValidation
                    ? " · 6성/최신 그룹 재확인"
                    : string.Empty;
                AppendBatchLog($"{link.NameHint} · 서브 {link.SubRating} / 리더 {link.LeaderRating} · {originalState}{validationText}");
            }
            if (selected.Length > 80)
            {
                AppendBatchLog($"… 외 {selected.Length - 80}개");
            }
        }
        catch (OperationCanceledException)
        {
            SetRatedSearchStatus("조건 검색을 중지했습니다.", isError: false);
        }
        catch (Exception exception)
        {
            SetRatedSearchStatus($"조건 검색 실패: {exception.Message}", isError: true);
            SetBatchSummary(exception.Message, isError: true);
        }
        finally
        {
            SetBatchBusy(false);
        }
    }

    private async void BatchDiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        List<string> sourceUrls = ParseBatchUrls(BatchUrlTextBox.Text);
        if (sourceUrls.Count == 0)
        {
            SetBatchSummary("GameWith 목록 또는 개별 캐릭터 주소를 입력하세요.", isError: true);
            BatchUrlTextBox.Focus();
            return;
        }

        _batchCancellation?.Cancel();
        _batchCancellation?.Dispose();
        _batchCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _batchCancellation.Token;
        var discovered = new Dictionary<string, GameWithCharacterLink>(StringComparer.OrdinalIgnoreCase);
        _discoveredGroupHints.Clear();
        _discoveredLinks.Clear();
        _originalOnlyCandidateUrls.Clear();
        int failureCount = 0;
        string lastFailureMessage = string.Empty;

        BatchLogTextBox.Clear();
        SetBatchBusy(true);
        SetBatchSummary($"{sourceUrls.Count}개 페이지에서 캐릭터 링크를 찾고 있습니다…", isError: false);

        try
        {
            for (int index = 0; index < sourceUrls.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourceUrl = sourceUrls[index];
                AppendBatchLog($"[{index + 1}/{sourceUrls.Count}] 링크 분석 중: {sourceUrl}");

                try
                {
                    IReadOnlyList<GameWithCharacterLink> links = await _service.DiscoverGameWithCharacterLinksAsync(
                        sourceUrl,
                        cancellationToken,
                        _knownGroups);
                    if (links.Count == 0)
                    {
                        failureCount++;
                        AppendBatchLog("  캐릭터 개별 링크를 찾지 못했습니다.");
                        continue;
                    }

                    foreach (GameWithCharacterLink link in links)
                    {
                        discovered[link.Url] = link;
                        _discoveredLinks[link.Url] = link;
                        if (!string.IsNullOrWhiteSpace(link.GroupHint))
                        {
                            _discoveredGroupHints[link.Url] = link.GroupHint.Trim();
                        }
                    }

                    AppendBatchLog($"  발견 {links.Count}개" +
                        (links.Count <= 8 && links.Any(link => link.NameHint.Length > 0)
                            ? $" · {string.Join(" · ", links.Select(link => link.NameHint).Where(name => name.Length > 0).Take(8))}"
                            : string.Empty));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failureCount++;
                    lastFailureMessage = exception.Message;
                    AppendBatchLog($"  실패 · {exception.Message}");
                }
            }

            if (discovered.Count == 0)
            {
                string message = lastFailureMessage.Length > 0
                    ? lastFailureMessage
                    : "캐릭터 링크를 찾지 못했습니다. 개별 캐릭터 URL을 직접 붙여 넣어도 됩니다.";
                SetBatchSummary(message, isError: true);
                return;
            }

            GameWithCharacterLink[] ordered = discovered.Values
                .OrderBy(link => link.NameHint, StringComparer.Ordinal)
                .ThenBy(link => link.Url, StringComparer.Ordinal)
                .ToArray();
            BatchUrlTextBox.Text = string.Join(Environment.NewLine, ordered.Select(link => link.Url));
            string[] detectedGroupHints = ordered
                .Select(link => link.GroupHint)
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string groupHintText = detectedGroupHints.Length == 1
                ? $" · 목록 페이지 소속 힌트: {detectedGroupHints[0]}"
                : string.Empty;
            string largeListWarning = ordered.Length > 100
                ? " · 100개가 넘습니다. 필요한 캐릭터만 남긴 뒤 연속 가져오기를 권장합니다."
                : string.Empty;
            SetBatchSummary(
                $"캐릭터 개별 링크 {ordered.Length}개를 찾았습니다.{groupHintText} 이제 '연속 가져오기 시작'을 누르세요.{largeListWarning}",
                isError: false);
            AppendBatchLog($"링크 정리 완료 · 중복 제거 후 {ordered.Length}개 · 분석 실패 {failureCount}개");
        }
        catch (OperationCanceledException)
        {
            SetBatchSummary("링크 찾기를 중지했습니다.", isError: false);
            AppendBatchLog("사용자가 링크 찾기를 중지했습니다.");
        }
        finally
        {
            SetBatchBusy(false);
        }
    }

    private async void BatchStartButton_Click(object sender, RoutedEventArgs e)
    {
        List<string> urls = ParseBatchUrls(BatchUrlTextBox.Text);
        if (urls.Count == 0)
        {
            SetBatchSummary("GameWith 개별 캐릭터 주소를 한 줄에 하나씩 입력하세요.", isError: true);
            BatchUrlTextBox.Focus();
            return;
        }

        CleanupBatchTemporaryFiles();
        _batchReviewItems.Clear();
        _lastFailedUrls.Clear();
        BatchReviewDataGrid.ItemsSource = null;
        DeleteTemporaryFile(_downloadedImagePath);
        _downloadedImagePath = string.Empty;
        _fetchedData = null;
        CharacterImagePreview.Source = null;
        ImagePlaceholderText.Visibility = Visibility.Visible;
        ApplyButton.IsEnabled = false;
        ImportedPreviews = Array.Empty<CharacterImportPreview>();
        BatchApplyButton.IsEnabled = false;
        BatchRetryFailedButton.IsEnabled = false;
        BatchLogTextBox.Clear();
        BatchProgressBar.Minimum = 0;
        BatchProgressBar.Maximum = urls.Count;
        BatchProgressBar.Value = 0;
        BatchProgressText.Text = $"0 / {urls.Count}";

        _batchCancellation?.Cancel();
        _batchCancellation?.Dispose();
        _batchCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _batchCancellation.Token;

        string category = CharacterCategories.Normalize(BatchCategoryComboBox.SelectedItem as string);
        bool downloadImages = BatchUseImageCheckBox.IsChecked == true;
        int delayMilliseconds = ParseDelayMilliseconds(BatchDelayTextBox.Text);
        var knownKeys = new HashSet<string>(_existingCharacterKeys, StringComparer.OrdinalIgnoreCase);
        int successCount = 0;
        int duplicateCount = 0;
        int failureCount = 0;
        int reviewCount = 0;
        int filteredCollaborationCount = 0;
        int filteredRecentACount = 0;
        int retryRecoveredCount = 0;
        int ratingFallbackCount = 0;
        bool wasCanceled = false;
        var failureReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        SetBatchBusy(true);
        SetBatchSummary($"{urls.Count}개 주소를 순서대로 확인합니다…", isError: false);

        try
        {
            for (int index = 0; index < urls.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string url = urls[index];
                int displayIndex = index + 1;
                BatchProgressText.Text = $"{displayIndex} / {urls.Count} 확인 중 · 항목당 최대 28초";
                AppendBatchLog($"[{displayIndex}/{urls.Count}] 가져오는 중: {url}");

                try
                {
                    BatchFetchResult fetchResult = await FetchBatchCharacterAsync(url, cancellationToken);
                    CharacterImportData? data = fetchResult.Data;
                    if (data is null)
                    {
                        failureCount++;
                        _lastFailedUrls.Add(url);
                        AddFailureReason(failureReasons, fetchResult.FailureReason);
                        AppendBatchLog($"  실패 · {fetchResult.FailureReason}");
                        continue;
                    }

                    if (fetchResult.RetryCount > 0)
                    {
                        retryRecoveredCount++;
                        AppendBatchLog($"  재시도 복구 · {fetchResult.RetryCount}회 추가 시도 후 성공");
                    }
                    if (fetchResult.UsedRatingTableFallback)
                    {
                        ratingFallbackCount++;
                        AppendBatchLog("  표 정보 복구 · 개별 페이지에서 빠진 이름/문자/속성을 평가표 정보로 보완");
                    }

                    if (_originalOnlyCandidateUrls.Contains(url))
                    {
                        bool? pageEvidence = data.IsCollaboration;
                        bool? rowEvidence = _discoveredLinks.TryGetValue(url, out GameWithCharacterLink? discoveredEvidence)
                            ? discoveredEvidence.IsCollaboration
                            : null;
                        bool? collaboration = MergeCollaborationEvidence(pageEvidence, rowEvidence);

                        if (collaboration == true)
                        {
                            filteredCollaborationCount++;
                            AppendBatchLog($"  건너뜀 · 명시적 콜라보 근거 확인: {data.Name}");
                            continue;
                        }
                        if (!collaboration.HasValue)
                        {
                            if (rowEvidence == true)
                            {
                                data.Notes.Add("목록 메타데이터에는 콜라보 후보 표시가 있었지만 개별 페이지에서 확정되지 않아 제외하지 않았습니다.");
                            }
                            data.Notes.Add("오리지널/콜라보를 자동 확정하지 못했지만 제외하지 않았습니다. 그룹이 없는 오리지널 캐릭터도 있으므로 검수표에서 필요할 때만 확인하세요.");
                        }
                    }

                    if (_discoveredGroupHints.TryGetValue(url, out string? sourceGroupHint) &&
                        !string.IsNullOrWhiteSpace(sourceGroupHint))
                    {
                        string normalizedHint = sourceGroupHint.Trim();
                        if (!string.Equals(data.GroupName, normalizedHint, StringComparison.OrdinalIgnoreCase))
                        {
                            data.Notes.Add(data.GroupName.Length > 0
                                ? $"개별 특성 추정 그룹 '{data.GroupName}' 대신 목록 페이지 소속 '{normalizedHint}'을 적용했습니다."
                                : $"목록 페이지에서 소속 그룹을 적용했습니다: {normalizedHint}");
                        }
                        data.GroupName = normalizedHint;
                        data.IncludedGroups = (data.IncludedGroups ?? new List<string>())
                            .Where(group => !string.Equals(group, normalizedHint, StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }

                    if (_discoveredLinks.TryGetValue(url, out GameWithCharacterLink? validationLink) &&
                        validationLink.RequiresRecentSixStarValidation)
                    {
                        if (!CharacterWebImportService.IsRecentSixStarAEligible(data, validationLink, out string validationReason))
                        {
                            filteredRecentACount++;
                            AppendBatchLog($"  건너뜀 · A 평가 추가 조건 불충족: {data.Name} · {validationReason}");
                            continue;
                        }

                        data.Notes.Add($"GameWith A 평가지만 세이유니마 이후 그룹의 6성으로 확인되어 포함했습니다. (소속 {data.GroupName}, 레어리티 {data.Rarity})");
                        AppendBatchLog($"  A 평가 포함 확인 · 6성 / {data.GroupName}");
                    }

                    List<string> letters = NormalizeImportedLetters(data.Letters);
                    string name = data.Name.Trim();
                    if (name.Length == 0 || letters.Count == 0)
                    {
                        failureCount++;
                        _lastFailedUrls.Add(url);
                        string reason = name.Length == 0
                            ? "이름을 인식하지 못함"
                            : "사용 문자를 인식하지 못함";
                        AddFailureReason(failureReasons, reason);
                        AppendBatchLog($"  실패 · {reason}");
                        continue;
                    }

                    string characterKey = BuildCharacterKey(name, letters);
                    if (!knownKeys.Add(characterKey))
                    {
                        duplicateCount++;
                        AppendBatchLog($"  건너뜀 · 이미 등록된 캐릭터: {name} ({string.Join("·", letters)})");
                        continue;
                    }

                    string downloadedImagePath = string.Empty;
                    if (downloadImages && data.ImageUrl.Length > 0)
                    {
                        try
                        {
                            downloadedImagePath = await DownloadSupportedImageAsync(data.ImageUrl, cancellationToken);
                        }
                        catch (Exception imageException) when (imageException is not OperationCanceledException)
                        {
                            data.Notes.Add($"이미지 자동 다운로드 실패: {imageException.Message}");
                        }
                    }

                    List<string> includedGroups = (data.IncludedGroups ?? new List<string>())
                        .Where(group => !string.IsNullOrWhiteSpace(group))
                        .Select(group => group.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var reviewItem = new CharacterImportReviewItem
                    {
                        IsSelected = true,
                        IsFavorite = false,
                        UseImage = downloadImages && data.ImageUrl.Length > 0,
                        Name = name,
                        Category = category,
                        Attribute = data.Attribute,
                        SubAttributesText = string.Join(" / ", data.SubAttributes ?? new List<string>()),
                        Species = data.Species,
                        GroupName = data.GroupName.Trim(),
                        LettersText = string.Join(" · ", letters),
                        IncludedGroupsText = string.Join(" · ", includedGroups),
                        ImageUrl = data.ImageUrl,
                        DownloadedImagePath = downloadedImagePath,
                        SourceUrl = data.SourceUrl,
                        SourceSite = data.SourceSite,
                        MatchedDatabaseUrl = data.MatchedDatabaseUrl,
                        GameWithRatingText = _discoveredLinks.TryGetValue(url, out GameWithCharacterLink? ratingLink)
                            ? $"서브 {ratingLink.SubRating} / 리더 {ratingLink.LeaderRating}"
                            : string.Empty,
                        Notes = (data.Notes ?? new List<string>()).ToList()
                    };
                    _batchReviewItems.Add(reviewItem);
                    successCount++;
                    if (reviewItem.NeedsReview)
                    {
                        reviewCount++;
                    }

                    string groupText = data.GroupName.Length > 0 ? data.GroupName : "그룹 없음(등록 가능)";
                    string attributeText = data.Attribute.Length > 0 ? data.Attribute : "속성?";
                    string speciesText = data.Species.Length > 0 ? data.Species : "종족?";
                    string imageText = downloadedImagePath.Length > 0 ? "이미지 포함" : "이미지 없음";
                    AppendBatchLog($"  성공 · {name} · {string.Join("·", letters)} · {attributeText}/{speciesText} · {groupText} · {imageText}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failureCount++;
                    _lastFailedUrls.Add(url);
                    string reason = SimplifyBatchFailureReason(exception.Message);
                    AddFailureReason(failureReasons, reason);
                    AppendBatchLog($"  실패 · {reason}");
                }
                finally
                {
                    BatchProgressBar.Value = displayIndex;
                    BatchProgressText.Text = $"{displayIndex} / {urls.Count}";
                }

                if (index < urls.Count - 1 && delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            wasCanceled = true;
            AppendBatchLog("사용자가 연속 가져오기를 중지했습니다.");
        }
        finally
        {
            BatchReviewDataGrid.ItemsSource = _batchReviewItems;
            SetBatchBusy(false);
            BatchApplyButton.IsEnabled = _batchReviewItems.Count > 0;
            BatchRetryFailedButton.IsEnabled = _lastFailedUrls.Count > 0;
            string prefix = wasCanceled ? "중지됨" : "완료";
            string reasonSummary = failureReasons.Count > 0
                ? " · 실패 원인 " + string.Join(", ", failureReasons.OrderByDescending(item => item.Value).Select(item => $"{item.Key} {item.Value}"))
                : string.Empty;
            SetBatchSummary(
                $"{prefix} · 검수 대상 {successCount}개 · 확인 권장 {reviewCount}개 · 중복 {duplicateCount}개 · 명시적 콜라보 제외 {filteredCollaborationCount}개 · 최신 6성 A 조건 제외 {filteredRecentACount}개 · 재시도 복구 {retryRecoveredCount}개 · 표 정보 복구 {ratingFallbackCount}개 · 실패 {failureCount}개{reasonSummary}" +
                (_batchReviewItems.Count > 0
                    ? " · 그룹은 비어 있어도 등록할 수 있습니다. 노란 행만 확인한 뒤 등록하세요."
                    : string.Empty),
                isError: _batchReviewItems.Count == 0 && failureCount > 0);
        }
    }

    private async Task<BatchFetchResult> FetchBatchCharacterAsync(string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 2;
        TimeSpan itemTimeout = TimeSpan.FromSeconds(28);
        string lastReason = "가져오기 실패";
        using var itemCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        itemCancellation.CancelAfter(itemTimeout);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                bool requiresRecentSixStarValidation =
                    _discoveredLinks.TryGetValue(url, out GameWithCharacterLink? validationLink) &&
                    validationLink.RequiresRecentSixStarValidation;

                // A 평가 후보도 우선 GameWith만 읽습니다.
                // GameWith에서 6성이 확인되지 않을 때만 짧은 제한 시간으로 DB 보강을 시도합니다.
                CharacterImportData data = await _service.ImportAsync(
                    url,
                    enrichFromDatabase: false,
                    databaseOverrideUrl: null,
                    cancellationToken: itemCancellation.Token,
                    knownGroups: _knownGroups,
                    knownGroupRelations: _knownGroupRelations);

                bool usedFallback = ApplyRatedTableHints(url, data);
                if (requiresRecentSixStarValidation && string.IsNullOrWhiteSpace(data.Rarity))
                {
                    await _service.TryEnrichFromDatabaseAsync(
                        data,
                        databaseOverrideUrl: null,
                        cancellationToken: itemCancellation.Token,
                        timeout: TimeSpan.FromSeconds(10));
                }

                return new BatchFetchResult(data, attempt - 1, usedFallback, string.Empty);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastReason = $"개별 캐릭터 처리 시간 초과({itemTimeout.TotalSeconds:0}초)";
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastReason = SimplifyBatchFailureReason(exception.Message);
                if (attempt < maxAttempts)
                {
                    try
                    {
                        await Task.Delay(900 * attempt, itemCancellation.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        lastReason = $"개별 캐릭터 처리 시간 초과({itemTimeout.TotalSeconds:0}초)";
                        break;
                    }
                }
            }
        }

        CharacterImportData? fallback = CreateRatedTableFallback(url, lastReason);
        return fallback is null
            ? new BatchFetchResult(null, maxAttempts - 1, false, lastReason)
            : new BatchFetchResult(fallback, maxAttempts - 1, true, string.Empty);
    }

    private bool ApplyRatedTableHints(string url, CharacterImportData data)
    {
        if (!_discoveredLinks.TryGetValue(url, out GameWithCharacterLink? link))
        {
            return false;
        }

        bool changed = false;
        if (string.IsNullOrWhiteSpace(data.Name) && !string.IsNullOrWhiteSpace(link.NameHint))
        {
            data.Name = link.NameHint.Trim();
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(data.Attribute) && !string.IsNullOrWhiteSpace(link.AttributeHint))
        {
            data.Attribute = DeckDataService.NormalizeAttribute(link.AttributeHint);
            changed = true;
        }

        List<string> currentLetters = NormalizeImportedLetters(data.Letters);
        List<string> hintedLetters = ParseLetters(link.LettersHint);
        List<string> mergedLetters = currentLetters
            .Concat(hintedLetters)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (mergedLetters.Count > currentLetters.Count)
        {
            data.Letters = mergedLetters;
            changed = true;
        }

        if (changed)
        {
            data.Notes.Add("GameWith 전 캐릭터 평가표의 이름/문자/속성 정보를 보조로 사용했습니다.");
        }
        return changed;
    }

    private CharacterImportData? CreateRatedTableFallback(string url, string failureReason)
    {
        if (!_discoveredLinks.TryGetValue(url, out GameWithCharacterLink? link))
        {
            return null;
        }

        List<string> letters = ParseLetters(link.LettersHint);
        if (string.IsNullOrWhiteSpace(link.NameHint) || letters.Count == 0)
        {
            return null;
        }

        return new CharacterImportData
        {
            Name = link.NameHint.Trim(),
            GroupName = link.GroupHint.Trim(),
            Attribute = DeckDataService.NormalizeAttribute(link.AttributeHint),
            Letters = letters,
            SourceUrl = url,
            SourceSite = "GameWith 평가표",
            IsCollaboration = link.IsCollaboration,
            Notes = new List<string>
            {
                $"개별 페이지 가져오기는 실패했지만 평가표 정보로 검수 항목을 만들었습니다: {failureReason}",
                "종족·이미지·그룹은 비어 있을 수 있으므로 등록 전 필요한 항목만 확인하세요."
            }
        };
    }

    private static bool? MergeCollaborationEvidence(bool? pageEvidence, bool? rowEvidence)
    {
        // 오리지널을 잘못 버리는 것보다 콜라보 후보가 검수표에 남는 편이 안전합니다.
        // 따라서 자동 제외는 개별 페이지가 명시적으로 콜라보라고 확인된 경우에만 합니다.
        if (pageEvidence == true)
        {
            return true;
        }
        if (pageEvidence == false || rowEvidence == false)
        {
            return false;
        }
        return null;
    }

    private static List<string> NormalizeImportedLetters(IEnumerable<string>? source) =>
        (source ?? Array.Empty<string>())
            .Select(KanaUtility.NormalizeCell)
            .Where(letter => letter.Length > 0 && KanaUtility.IsJapaneseCell(letter))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static void AddFailureReason(IDictionary<string, int> counts, string reason)
    {
        string key = string.IsNullOrWhiteSpace(reason) ? "기타" : reason.Trim();
        counts[key] = counts.TryGetValue(key, out int current) ? current + 1 : 1;
    }

    private static string SimplifyBatchFailureReason(string message)
    {
        string text = (message ?? string.Empty).Trim();
        if (text.Contains("429", StringComparison.OrdinalIgnoreCase)) return "요청 제한(429)";
        if (text.Contains("timeout", StringComparison.OrdinalIgnoreCase) || text.Contains("시간", StringComparison.Ordinal)) return "시간 초과";
        if (text.Contains("404", StringComparison.OrdinalIgnoreCase)) return "페이지 없음(404)";
        if (text.Contains("이름", StringComparison.Ordinal)) return "이름 인식 실패";
        if (text.Contains("문자", StringComparison.Ordinal)) return "문자 인식 실패";
        if (text.Contains("페이지를 읽지 못", StringComparison.Ordinal)) return "페이지 요청 실패";
        return text.Length > 80 ? text[..80] : (text.Length > 0 ? text : "기타");
    }

    private void BatchRetryFailedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFailedUrls.Count == 0)
        {
            SetBatchSummary("다시 시도할 실패 URL이 없습니다.", isError: false);
            return;
        }

        BatchUrlTextBox.Text = string.Join(Environment.NewLine, _lastFailedUrls.Distinct(StringComparer.OrdinalIgnoreCase));
        SetBatchSummary($"실패 URL {_lastFailedUrls.Count}개만 입력했습니다. 현재 검수 결과를 먼저 등록한 뒤, 다시 열어서 요청 간격을 1200ms 이상으로 올려 재시도하는 것을 권장합니다.", isError: false);
        BatchUrlTextBox.Focus();
    }

    private sealed record BatchFetchResult(
        CharacterImportData? Data,
        int RetryCount,
        bool UsedRatingTableFallback,
        string FailureReason);

    private void BatchCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _batchCancellation?.Cancel();
    }

    private void BatchSelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        CommitBatchReviewEdits();
        foreach (CharacterImportReviewItem item in _batchReviewItems)
        {
            item.IsSelected = true;
        }
    }

    private void BatchSelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        CommitBatchReviewEdits();
        foreach (CharacterImportReviewItem item in _batchReviewItems)
        {
            item.IsSelected = false;
        }
    }

    private void BatchApplyButton_Click(object sender, RoutedEventArgs e)
    {
        CommitBatchReviewEdits();
        List<CharacterImportReviewItem> selectedItems = _batchReviewItems
            .Where(item => item.IsSelected)
            .ToList();
        if (selectedItems.Count == 0)
        {
            SetBatchSummary("등록할 항목을 하나 이상 체크하세요.", isError: true);
            return;
        }

        var previews = new List<CharacterImportPreview>();
        foreach (CharacterImportReviewItem item in selectedItems)
        {
            string name = item.Name.Trim();
            List<string> letters = ParseLetters(item.LettersText);
            if (name.Length == 0 || letters.Count == 0)
            {
                SetBatchSummary($"'{(name.Length > 0 ? name : "이름 없음")}' 항목의 이름/문자를 확인하세요.", isError: true);
                return;
            }

            string reviewedImagePath = item.UseImage ? item.DownloadedImagePath : string.Empty;
            if (!item.UseImage)
            {
                DeleteTemporaryFile(item.DownloadedImagePath);
                item.DownloadedImagePath = string.Empty;
            }

            previews.Add(new CharacterImportPreview
            {
                Name = name,
                Category = CharacterCategories.Normalize(item.Category),
                Attribute = item.Attribute,
                SubAttributes = ParseSubAttributes(item.SubAttributesText, item.Attribute),
                Species = item.Species,
                IsFavorite = item.IsFavorite,
                GroupName = item.GroupName.Trim(),
                IncludedGroups = ParseGroups(item.IncludedGroupsText),
                Letters = letters,
                ImageUrl = item.ImageUrl,
                DownloadedImagePath = reviewedImagePath,
                SourceUrl = item.SourceUrl,
                SourceSite = item.SourceSite,
                MatchedDatabaseUrl = item.MatchedDatabaseUrl,
                Notes = item.Notes.ToList()
            });
        }

        foreach (CharacterImportReviewItem item in _batchReviewItems.Where(item => !item.IsSelected))
        {
            DeleteTemporaryFile(item.DownloadedImagePath);
            item.DownloadedImagePath = string.Empty;
        }

        ImportedPreview = null;
        ImportedPreviews = previews;
        DialogResult = true;
        Close();
    }

    private void CommitBatchReviewEdits()
    {
        BatchReviewDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        BatchReviewDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _fetchCancellation?.Cancel();
        _fetchCancellation?.Dispose();
        _batchCancellation?.Cancel();
        _batchCancellation?.Dispose();

        if (DialogResult != true)
        {
            DeleteTemporaryFile(_downloadedImagePath);
            CleanupBatchTemporaryFiles();
        }

        base.OnClosed(e);
    }

    private async Task<string> DownloadSupportedImageAsync(
        string imageUrl,
        CancellationToken cancellationToken)
    {
        string path = await _service.DownloadImageToTemporaryFileAsync(imageUrl, cancellationToken);
        if (path.Length == 0)
        {
            return string.Empty;
        }

        if (!CharacterImageService.IsSupportedImageFile(path))
        {
            DeleteTemporaryFile(path);
            return string.Empty;
        }

        if (!CharacterImageService.IsLikelyCharacterArtwork(path, out int width, out int height))
        {
            DeleteTemporaryFile(path);
            throw new InvalidDataException(
                $"캐릭터 일러스트보다 작은 이미지({width}x{height})가 감지되어 제외했습니다. 속성/종족 아이콘일 가능성이 높습니다.");
        }

        return path;
    }

    private GameWithRatingMatchMode ReadRatingMatchMode()
    {
        if (RatedMatchModeComboBox.SelectedItem is System.Windows.Controls.ComboBoxItem item)
        {
            string tag = item.Tag?.ToString() ?? string.Empty;
            if (Enum.TryParse(tag, ignoreCase: true, out GameWithRatingMatchMode mode))
            {
                return mode;
            }
        }
        return GameWithRatingMatchMode.Either;
    }

    private static string NormalizeCharacterNameKey(string? name)
        => Regex.Replace(
                (name ?? string.Empty).Normalize(NormalizationForm.FormKC),
                @"[\s・･·⋅]+",
                string.Empty,
                RegexOptions.CultureInvariant)
            .Trim()
            .ToUpperInvariant();

    private void SetRatedSearchStatus(string message, bool isError)
    {
        RatedSearchStatusText.Text = message;
        RatedSearchStatusText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(isError ? "#FF8D8D" : "#8FE3B1"));
    }

    private static List<string> ParseBatchUrls(string text)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     text ?? string.Empty,
                     "https?://[^\\s<>\"']+",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string candidate = match.Value.TrimEnd(',', '，', '、', ';', '；', ')', ']', '}');
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
                uri is null ||
                !uri.Host.EndsWith("gamewith.jp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string normalized = uri.ToString();
            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static List<string> ParseLetters(string text)
    {
        string normalized = KanaUtility.ToHiraganaEquivalent(
            KanaUtility.ConvertHangulToKana(text ?? string.Empty));
        return KanaUtility.SplitIntoCells(normalized)
            .Select(KanaUtility.NormalizeCell)
            .Where(letter => letter.Length > 0 && KanaUtility.IsJapaneseCell(letter))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string ReadMetadataCombo(System.Windows.Controls.ComboBox comboBox, bool isAttribute)
    {
        string raw = comboBox.SelectedItem as string ?? comboBox.Text ?? string.Empty;
        if (string.Equals(raw, "미입력", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return isAttribute
            ? DeckDataService.NormalizeAttribute(raw)
            : DeckDataService.NormalizeSpecies(raw);
    }

    private static List<string> ParseSubAttributes(string text, string? mainAttribute)
    {
        string main = DeckDataService.NormalizeAttribute(mainAttribute);
        return (text ?? string.Empty)
            .Split(new[] { ',', '，', '、', '·', '・', '/', '／', '|', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(DeckDataService.NormalizeAttribute)
            .Where(value => value.Length > 0 && !string.Equals(value, main, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> ParseGroups(string text)
        => (text ?? string.Empty)
            .Replace("または", "·", StringComparison.Ordinal)
            .Split(new[] { ',', '，', '、', '·', '・', '/', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(group => group.Trim())
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildCharacterKey(string name, IEnumerable<string>? letters)
    {
        string normalizedName = (name ?? string.Empty).Trim().ToUpperInvariant();
        string normalizedLetters = string.Join(
            "|",
            (letters ?? Array.Empty<string>())
                .Select(KanaUtility.NormalizeCell)
                .Where(letter => letter.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(letter => letter, StringComparer.Ordinal));
        return $"{normalizedName}\u001F{normalizedLetters}";
    }

    private static int ParseDelayMilliseconds(string text)
    {
        if (!int.TryParse(text?.Trim(), out int milliseconds))
        {
            return 700;
        }

        return Math.Clamp(milliseconds, 200, 5000);
    }

    private void SetBusy(bool busy)
    {
        FetchButton.IsEnabled = !busy && _batchCancellation is null;
        ApplyButton.IsEnabled = !busy && _fetchedData is not null;
        SourceUrlTextBox.IsEnabled = !busy;
        BatchStartButton.IsEnabled = !busy && _batchCancellation is null;
        BatchRetryFailedButton.IsEnabled = !busy && _batchCancellation is null && _lastFailedUrls.Count > 0;
        BatchDiscoverButton.IsEnabled = !busy && _batchCancellation is null;
        RatedDiscoverButton.IsEnabled = !busy && _batchCancellation is null;
        BatchReviewDataGrid.IsEnabled = !busy;
        BatchSelectAllButton.IsEnabled = !busy;
        BatchSelectNoneButton.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void SetBatchBusy(bool busy)
    {
        if (!busy)
        {
            _batchCancellation?.Dispose();
            _batchCancellation = null;
        }

        BatchStartButton.IsEnabled = !busy;
        BatchRetryFailedButton.IsEnabled = !busy && _lastFailedUrls.Count > 0;
        BatchDiscoverButton.IsEnabled = !busy;
        RatedDiscoverButton.IsEnabled = !busy;
        BatchCancelButton.IsEnabled = busy;
        BatchReviewDataGrid.IsEnabled = !busy;
        BatchSelectAllButton.IsEnabled = !busy;
        BatchSelectNoneButton.IsEnabled = !busy;
        BatchApplyButton.IsEnabled = !busy && _batchReviewItems.Count > 0;
        BatchUrlTextBox.IsEnabled = !busy;
        BatchCategoryComboBox.IsEnabled = !busy;
        BatchUseImageCheckBox.IsEnabled = !busy;
        BatchDelayTextBox.IsEnabled = !busy;
        RatedSSSCheckBox.IsEnabled = !busy;
        RatedSSCheckBox.IsEnabled = !busy;
        RatedSCheckBox.IsEnabled = !busy;
        RatedRecentSixStarACheckBox.IsEnabled = !busy;
        RatedMatchModeComboBox.IsEnabled = !busy;
        RatedOriginalOnlyCheckBox.IsEnabled = !busy;
        RatedExcludeRegisteredCheckBox.IsEnabled = !busy;
        FetchButton.IsEnabled = !busy;
        SourceUrlTextBox.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && _fetchedData is not null;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void SetStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(isError ? "#FF8D8D" : "#8FE3B1"));
    }

    private void SetBatchSummary(string message, bool isError)
    {
        BatchSummaryText.Text = message;
        BatchSummaryText.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(isError ? "#FF8D8D" : "#8FE3B1"));
    }

    private void AppendWarning(string message)
    {
        string line = $"• {message}";
        WarningText.Text = string.IsNullOrWhiteSpace(WarningText.Text)
            ? line
            : WarningText.Text + Environment.NewLine + line;
    }

    private void AppendBatchLog(string message)
    {
        BatchLogTextBox.AppendText(message + Environment.NewLine);
        BatchLogTextBox.ScrollToEnd();
    }

    private void CleanupBatchTemporaryFiles()
    {
        foreach (CharacterImportReviewItem item in _batchReviewItems)
        {
            DeleteTemporaryFile(item.DownloadedImagePath);
            item.DownloadedImagePath = string.Empty;
        }
    }

    private static void DeleteTemporaryFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 임시 이미지 삭제 실패는 등록을 막지 않습니다.
        }
    }
}
