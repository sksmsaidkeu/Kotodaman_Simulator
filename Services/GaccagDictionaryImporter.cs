using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder.Services;

public sealed class GaccagDictionaryImporter
{
    public const string SourceUrl = "https://gaccag.com/kotodaman/dictionary/";

    /// <summary>
    /// Edge의 실제 렌더링 결과가 필요한 페이지를 한 번 열어 최종 DOM HTML을 반환합니다.
    /// GameWith처럼 일부 목록을 JavaScript로 채우는 페이지의 보조 수집용입니다.
    /// </summary>
    public static async Task<string> RenderPageHtmlAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri is null)
        {
            throw new ArgumentException("올바른 웹 주소가 아닙니다.", nameof(url));
        }

        string edgePath = FindEdgeExecutable()
            ?? throw new FileNotFoundException(
                "Microsoft Edge 실행 파일을 찾지 못했습니다. Edge가 설치되어 있는지 확인해주세요.");

        await using EdgeDevToolsBrowser browser = await EdgeDevToolsBrowser.StartAsync(
            edgePath,
            cancellationToken);
        await browser.NavigateAsync(uri.ToString(), cancellationToken);
        return await browser.WaitForRenderedHtmlAsync(cancellationToken);
    }

    // GACCAG 검색 결과는 한 번에 약 150행입니다.
    // 사이트가 긴 접두어를 제대로 반영하지 않는 경우가 있으므로,
    // 자동 분할은 최대 2글자 접두어까지만 허용합니다.
    private const int ResultPageLimit = 150;
    private const int MaximumPrefixCells = 2;
    private const int SaveEveryQueries = 100;
    private const int DirectExtractionSuccessCount = 30000;

    // 단어 첫 글자로 현실적으로 사용되는 문자입니다.
    // 작은 가나와 장음부호는 첫 글자 검색에서 제외해 불필요한 요청을 줄입니다.
    private static readonly string[] InitialKana = (
        "あ い う え お か き く け こ さ し す せ そ た ち つ て と な に ぬ ね の は ひ ふ へ ほ ま み む め も や ゆ よ ら り る れ ろ わ を ん " +
        "が ぎ ぐ げ ご ざ じ ず ぜ ぞ だ ぢ づ で ど ば び ぶ べ ぼ ぱ ぴ ぷ ぺ ぽ ゔ")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    // 두 번째 글자 이후에는 작은 가나와 장음부호도 실제 문자로 사용될 수 있습니다.
    private static readonly string[] ContinuationKana = (
        "あ い う え お か き く け こ さ し す せ そ た ち つ て と な に ぬ ね の は ひ ふ へ ほ ま み む め も や ゆ よ ら り る れ ろ わ を ん " +
        "が ぎ ぐ げ ご ざ じ ず ぜ ぞ だ ぢ づ で ど ば び ぶ べ ぼ ぱ ぴ ぷ ぺ ぽ " +
        "ぁ ぃ ぅ ぇ ぉ っ ゃ ゅ ょ ゎ ゔ ゐ ゑ ー")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true
    };

    public async Task<GaccagImportResult> ImportAsync(
        string destinationPath,
        string metadataPath,
        string checkpointPath,
        GaccagImportMode mode,
        IProgress<GaccagImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int[] targetLengths = GetTargetLengths(mode);
        string modeKey = mode.ToString();
        string modeLabel = GetModeLabel(mode);
        string edgePath = FindEdgeExecutable()
            ?? throw new FileNotFoundException(
                "Microsoft Edge 실행 파일을 찾지 못했습니다. Edge가 설치되어 있는지 확인해주세요.");

        List<WordEntry> existingWords = await LoadWordsAsync(destinationPath, cancellationToken);
        int originalWordCount = existingWords.Count;
        var collected = new ConcurrentDictionary<string, WordEntry>(
            existingWords
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .GroupBy(word => word.Text, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToDictionary(word => word.Text, StringComparer.Ordinal),
            StringComparer.Ordinal);

        GaccagImportCheckpoint checkpoint = await LoadCheckpointAsync(
            checkpointPath,
            modeKey,
            cancellationToken);

        // 이전 버전의 무한 분할 진행 기록은 수십만 건까지 커질 수 있습니다.
        // 2글자를 초과하는 접두어는 버리고 안전한 범위만 이어받습니다.
        checkpoint.CompletedQueries = checkpoint.CompletedQueries
            .Where(value => TryGetCheckpointPrefixCellCount(value, out int cells)
                            && cells <= MaximumPrefixCells)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        checkpoint.PendingQueries = checkpoint.PendingQueries
            .Where(value => TryGetCheckpointPrefixCellCount(value, out int cells)
                            && cells <= MaximumPrefixCells)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        checkpoint.QueryCount = Math.Min(checkpoint.QueryCount, 100000);

        var completedQueries = new HashSet<string>(
            checkpoint.CompletedQueries,
            StringComparer.Ordinal);
        var scheduledQueries = new HashSet<string>(StringComparer.Ordinal);
        var jobs = new Queue<(int Length, string Prefix)>();
        var saveLock = new SemaphoreSlim(1, 1);
        var additionalBrowsers = new List<EdgeDevToolsBrowser>();

        int queryCount = Math.Max(0, checkpoint.QueryCount);
        int estimatedQueries = queryCount;
        int lastSavedQueryCount = queryCount;
        bool completedSuccessfully = false;
        EdgeDevToolsBrowser? primaryBrowser = null;

        void EnqueueJob(int length, string prefix)
        {
            string key = BuildQueryKey(length, prefix);
            if (completedQueries.Contains(key) || !scheduledQueries.Add(key))
            {
                return;
            }

            jobs.Enqueue((length, prefix));
            estimatedQueries++;
        }

        bool TryGetCheckpointPrefixCellCount(string value, out int cellCount)
        {
            cellCount = 0;
            int separatorIndex = value.IndexOf('|');
            if (separatorIndex <= 0 || separatorIndex >= value.Length - 1)
            {
                return false;
            }

            string prefix = value[(separatorIndex + 1)..];
            cellCount = KanaUtility.SplitIntoCells(prefix).Count;
            return cellCount > 0;
        }

        bool TryParseCheckpointQuery(string value, out int length, out string prefix)
        {
            length = 0;
            prefix = string.Empty;
            int separatorIndex = value.IndexOf('|');
            if (separatorIndex <= 0
                || !int.TryParse(value[..separatorIndex], out length)
                || length is < 2 or > 7)
            {
                return false;
            }

            prefix = value[(separatorIndex + 1)..];
            return !string.IsNullOrWhiteSpace(prefix);
        }

        if (checkpoint.PendingQueries.Count > 0)
        {
            foreach (string pending in checkpoint.PendingQueries)
            {
                if (TryParseCheckpointQuery(pending, out int length, out string prefix)
                    && targetLengths.Contains(length)
                    && KanaUtility.SplitIntoCells(prefix).Count <= MaximumPrefixCells)
                {
                    EnqueueJob(length, prefix);
                }
            }
        }

        if (jobs.Count == 0)
        {
            foreach (int length in targetLengths)
            {
                foreach (string prefix in InitialKana)
                {
                    EnqueueJob(length, prefix);
                }
            }
        }

        try
        {
            progress?.Report(new GaccagImportProgress
            {
                Message = $"{modeLabel} · 사이트 내부 사전 파일을 먼저 확인합니다.",
                CompletedQueries = queryCount,
                EstimatedQueries = Math.Max(queryCount + 1, estimatedQueries),
                CollectedWords = collected.Count,
                AddedWords = Math.Max(0, collected.Count - originalWordCount)
            });

            primaryBrowser = await EdgeDevToolsBrowser.StartAsync(edgePath, cancellationToken);
            await primaryBrowser.NavigateAsync(SourceUrl, cancellationToken);
            await primaryBrowser.WaitForDictionaryPageAsync(cancellationToken);

            IReadOnlyList<GaccagRow> directRows =
                await primaryBrowser.TryExtractCompleteDictionaryAsync(cancellationToken);
            AddRows(directRows);

            int directDistinctCount = directRows
                .Select(row => row.Kana)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .Count();
            int directTargetCount = directRows.Count(row => targetLengths.Contains(row.Length));
            bool directExtractionSucceeded = directDistinctCount >= DirectExtractionSuccessCount
                                             && directDistinctCount > originalWordCount
                                             && directTargetCount > 0;

            if (directExtractionSucceeded)
            {
                progress?.Report(new GaccagImportProgress
                {
                    Message = $"사이트 내부 데이터에서 {directDistinctCount:N0}개를 직접 추출했습니다.",
                    CompletedQueries = queryCount,
                    EstimatedQueries = Math.Max(queryCount + 1, estimatedQueries),
                    CollectedWords = collected.Count,
                    AddedWords = Math.Max(0, collected.Count - originalWordCount)
                });
            }
            else
            {
                bool fastBatchSupported = false;
                try
                {
                    fastBatchSupported = await primaryBrowser.CanUseFastBatchSearchAsync(
                        targetLengths[0],
                        cancellationToken);
                }
                catch
                {
                    fastBatchSupported = false;
                }

                if (fastBatchSupported)
                {
                    progress?.Report(new GaccagImportProgress
                    {
                        Message = "초고속 직접 요청 모드 · 화면 조작 없이 검색 결과를 묶어서 받습니다.",
                        CompletedQueries = queryCount,
                        EstimatedQueries = Math.Max(queryCount + 1, estimatedQueries),
                        CollectedWords = collected.Count,
                        AddedWords = Math.Max(0, collected.Count - originalWordCount)
                    });

                    while (jobs.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        List<(int Length, string Prefix)> batch = TakeBatch(32);

                        try
                        {
                            IReadOnlyList<SearchPageResult> results =
                                await primaryBrowser.SearchBatchAsync(batch, cancellationToken);

                            if (results.Count != batch.Count)
                            {
                                throw new InvalidOperationException(
                                    "GACCAG에서 요청한 검색 묶음과 다른 수의 결과를 반환했습니다.");
                            }

                            for (int index = 0; index < batch.Count; index++)
                            {
                                ProcessSearchResult(batch[index], results[index]);
                            }
                        }
                        catch
                        {
                            RequeueBatch(batch);
                            throw;
                        }

                        await SaveIfNeededAsync();
                    }
                }
                else
                {
                    progress?.Report(new GaccagImportProgress
                    {
                        Message = "직접 요청을 사용할 수 없어 호환 병렬 모드로 전환합니다.",
                        CompletedQueries = queryCount,
                        EstimatedQueries = Math.Max(queryCount + 1, estimatedQueries),
                        CollectedWords = collected.Count,
                        AddedWords = Math.Max(0, collected.Count - originalWordCount)
                    });

                    const int legacyWorkerCount = 6;
                    for (int index = 1; index < legacyWorkerCount; index++)
                    {
                        EdgeDevToolsBrowser browser =
                            await EdgeDevToolsBrowser.StartAsync(edgePath, cancellationToken);
                        await browser.NavigateAsync(SourceUrl, cancellationToken);
                        await browser.WaitForDictionaryPageAsync(cancellationToken);
                        additionalBrowsers.Add(browser);
                    }

                    var browsers = new List<EdgeDevToolsBrowser> { primaryBrowser };
                    browsers.AddRange(additionalBrowsers);

                    while (jobs.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        List<(int Length, string Prefix)> batch = TakeBatch(browsers.Count);

                        progress?.Report(new GaccagImportProgress
                        {
                            Message = $"호환 병렬 모드 · {batch.Count}개 검색을 동시에 처리 중",
                            CompletedQueries = queryCount,
                            EstimatedQueries = Math.Max(queryCount + 1, estimatedQueries),
                            CollectedWords = collected.Count,
                            AddedWords = Math.Max(0, collected.Count - originalWordCount)
                        });

                        try
                        {
                            Task<SearchPageResult>[] tasks = batch
                                .Select((job, index) => browsers[index].SearchAsync(
                                    job.Prefix,
                                    job.Length,
                                    cancellationToken))
                                .ToArray();
                            SearchPageResult[] results = await Task.WhenAll(tasks);

                            for (int index = 0; index < batch.Count; index++)
                            {
                                ProcessSearchResult(batch[index], results[index]);
                            }
                        }
                        catch
                        {
                            RequeueBatch(batch);
                            throw;
                        }

                        await SaveIfNeededAsync();
                    }
                }
            }

            await PersistAsync(isPartial: false, saveCancellationToken: cancellationToken);
            completedSuccessfully = true;

            if (File.Exists(checkpointPath))
            {
                File.Delete(checkpointPath);
            }

            GaccagImportResult result = BuildResult(isPartial: false);
            progress?.Report(new GaccagImportProgress
            {
                Message = $"{modeLabel} 완료 · 새 단어 {result.AddedWordCount:N0}개 추가",
                CompletedQueries = queryCount,
                EstimatedQueries = Math.Max(queryCount, estimatedQueries),
                CollectedWords = result.WordCount,
                AddedWords = result.AddedWordCount
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            await PersistAsync(isPartial: true, saveCancellationToken: CancellationToken.None);
            throw;
        }
        catch
        {
            await PersistAsync(isPartial: true, saveCancellationToken: CancellationToken.None);
            throw;
        }
        finally
        {
            foreach (EdgeDevToolsBrowser browser in additionalBrowsers)
            {
                await browser.DisposeAsync();
            }

            if (primaryBrowser is not null)
            {
                await primaryBrowser.DisposeAsync();
            }

            saveLock.Dispose();

            if (!completedSuccessfully)
            {
                progress?.Report(new GaccagImportProgress
                {
                    Message = "중간 저장 완료 · 같은 모드로 다시 누르면 이어서 진행합니다.",
                    CompletedQueries = queryCount,
                    EstimatedQueries = Math.Max(queryCount, estimatedQueries),
                    CollectedWords = collected.Count,
                    AddedWords = Math.Max(0, collected.Count - originalWordCount)
                });
            }
        }

        List<(int Length, string Prefix)> TakeBatch(int maximumCount)
        {
            var batch = new List<(int Length, string Prefix)>(maximumCount);
            while (batch.Count < maximumCount && jobs.Count > 0)
            {
                batch.Add(jobs.Dequeue());
            }
            return batch;
        }

        void RequeueBatch(IEnumerable<(int Length, string Prefix)> batch)
        {
            foreach ((int length, string prefix) in batch)
            {
                if (!completedQueries.Contains(BuildQueryKey(length, prefix)))
                {
                    jobs.Enqueue((length, prefix));
                }
            }
        }

        void ProcessSearchResult(
            (int Length, string Prefix) job,
            SearchPageResult searchResult)
        {
            queryCount++;
            int prefixCells = KanaUtility.SplitIntoCells(job.Prefix).Count;
            IReadOnlyList<GaccagRow> validRows = searchResult.Rows
                .Where(row => row.Length == job.Length)
                .Where(row => KanaUtility.SplitIntoCells(row.Kana).Count == job.Length)
                .ToArray();

            AddRows(validRows);

            bool pageReachedLimit = searchResult.Rows.Count is >= ResultPageLimit and <= ResultPageLimit + 10;
            bool prefixWasReallyApplied = validRows.Count > 0
                                          && validRows.All(row =>
                                              row.Kana.StartsWith(job.Prefix, StringComparison.Ordinal));
            bool shouldSplit = pageReachedLimit
                               && prefixWasReallyApplied
                               && prefixCells < Math.Min(job.Length, MaximumPrefixCells);
            string queryKey = BuildQueryKey(job.Length, job.Prefix);

            // 현재 검색은 처리 완료로 기록하고, 필요한 경우에만 한 단계 자식 검색을 추가합니다.
            // 이렇게 해야 중단 후 재개할 때 부모 검색이 다시 무한 반복되지 않습니다.
            completedQueries.Add(queryKey);

            if (shouldSplit)
            {
                foreach (string nextLetter in ContinuationKana)
                {
                    EnqueueJob(job.Length, job.Prefix + nextLetter);
                }
            }

            progress?.Report(new GaccagImportProgress
            {
                Message = shouldSplit
                    ? $"{job.Length}글자 '{job.Prefix}' 결과가 잘려 다음 글자로 분할했습니다."
                    : $"{job.Length}글자 '{job.Prefix}' 완료 · {validRows.Count:N0}개 확인",
                CompletedQueries = queryCount,
                EstimatedQueries = Math.Max(queryCount + jobs.Count, estimatedQueries),
                CollectedWords = collected.Count,
                AddedWords = Math.Max(0, collected.Count - originalWordCount)
            });
        }

        async Task SaveIfNeededAsync()
        {
            if (queryCount - lastSavedQueryCount < SaveEveryQueries)
            {
                return;
            }

            await PersistAsync(isPartial: true, saveCancellationToken: CancellationToken.None);
            lastSavedQueryCount = queryCount;
        }

        void AddRows(IEnumerable<GaccagRow> rows)
        {
            foreach (GaccagRow row in rows)
            {
                int actualLength = KanaUtility.SplitIntoCells(row.Kana).Count;
                if (actualLength is < 2 or > 7)
                {
                    continue;
                }

                string text = row.Kana.Trim().Normalize(NormalizationForm.FormC);
                if (text.Length == 0 || !IsDictionaryKana(text))
                {
                    continue;
                }

                string? description = string.IsNullOrWhiteSpace(row.Display)
                    ? null
                    : row.Display.Trim();

                collected.AddOrUpdate(
                    text,
                    _ => new WordEntry
                    {
                        Text = text,
                        Description = description,
                        Source = "GACCAG"
                    },
                    (_, existing) =>
                    {
                        if (string.IsNullOrWhiteSpace(existing.Description)
                            && !string.IsNullOrWhiteSpace(description))
                        {
                            existing.Description = description;
                        }
                        return existing;
                    });
            }
        }

        async Task PersistAsync(bool isPartial, CancellationToken saveCancellationToken)
        {
            await saveLock.WaitAsync(saveCancellationToken);
            try
            {
                WordEntry[] orderedWords = collected.Values
                    .Where(word => KanaUtility.SplitIntoCells(word.Text).Count is >= 2 and <= 7)
                    .OrderBy(word => word.Text, StringComparer.Ordinal)
                    .ToArray();

                await SaveSafelyAsync(destinationPath, orderedWords, saveCancellationToken);

                int shortWordCount = orderedWords.Count(word =>
                    KanaUtility.SplitIntoCells(word.Text).Count is >= 2 and <= 3);
                int searchWordCount = orderedWords.Length - shortWordCount;
                var now = DateTimeOffset.Now;

                var metadata = new GaccagUpdateMetadata
                {
                    SourceUrl = SourceUrl,
                    UpdatedAt = now,
                    WordCount = orderedWords.Length,
                    ShortWordCount = shortWordCount,
                    SearchWordCount = searchWordCount,
                    MinimumLength = 2,
                    MaximumLength = 7,
                    QueryCount = queryCount,
                    LastMode = modeKey,
                    IsPartial = isPartial
                };
                await SaveSafelyAsync(metadataPath, metadata, saveCancellationToken);

                if (isPartial)
                {
                    var checkpointValue = new GaccagImportCheckpoint
                    {
                        Mode = modeKey,
                        UpdatedAt = now,
                        QueryCount = queryCount,
                        CompletedQueries = completedQueries
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToList(),
                        PendingQueries = jobs
                            .Select(job => BuildQueryKey(job.Length, job.Prefix))
                            .Distinct(StringComparer.Ordinal)
                            .ToList()
                    };
                    await SaveSafelyAsync(
                        checkpointPath,
                        checkpointValue,
                        saveCancellationToken);
                }
            }
            finally
            {
                saveLock.Release();
            }
        }

        GaccagImportResult BuildResult(bool isPartial)
        {
            WordEntry[] words = collected.Values.ToArray();
            int shortWordCount = words.Count(word =>
                KanaUtility.SplitIntoCells(word.Text).Count is >= 2 and <= 3);
            return new GaccagImportResult
            {
                WordCount = words.Length,
                ShortWordCount = shortWordCount,
                SearchWordCount = words.Length - shortWordCount,
                AddedWordCount = Math.Max(0, words.Length - originalWordCount),
                QueryCount = queryCount,
                IsPartial = isPartial,
                UpdatedAt = DateTimeOffset.Now,
                SourceUrl = SourceUrl
            };
        }
    }

    private static int[] GetTargetLengths(GaccagImportMode mode)
        => mode switch
        {
            GaccagImportMode.SevenLetters => new[] { 7 },
            GaccagImportMode.SixLetters => new[] { 6 },
            GaccagImportMode.FiveLetters => new[] { 5 },
            GaccagImportMode.FourLetters => new[] { 4 },
            GaccagImportMode.SearchWords => new[] { 6, 5, 4 },
            GaccagImportMode.ComboWords => new[] { 3, 2 },
            _ => new[] { 6, 5, 4 }
        };

    public static string GetModeLabel(GaccagImportMode mode)
        => mode switch
        {
            GaccagImportMode.SevenLetters => "7글자 재검사",
            GaccagImportMode.SixLetters => "6글자 안전 보충",
            GaccagImportMode.FiveLetters => "5글자 안전 보충",
            GaccagImportMode.FourLetters => "4글자 안전 보충",
            GaccagImportMode.SearchWords => "4~6글자 순차 보충",
            GaccagImportMode.ComboWords => "2~3글자 콤보용 수집",
            _ => "GACCAG 업데이트"
        };

    private static string BuildQueryKey(int length, string prefix)
        => $"{length}|{prefix}";

    private async Task<List<WordEntry>> LoadWordsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new List<WordEntry>();
        }

        try
        {
            await using FileStream fileStream = File.OpenRead(path);
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var gzipStream = new GZipStream(
                    fileStream,
                    CompressionMode.Decompress,
                    leaveOpen: false);
                return await JsonSerializer.DeserializeAsync<List<WordEntry>>(
                           gzipStream,
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                           cancellationToken)
                       ?? new List<WordEntry>();
            }

            return await JsonSerializer.DeserializeAsync<List<WordEntry>>(
                       fileStream,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                       cancellationToken)
                   ?? new List<WordEntry>();
        }
        catch
        {
            return new List<WordEntry>();
        }
    }

    private async Task<GaccagImportCheckpoint> LoadCheckpointAsync(
        string path,
        string modeKey,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new GaccagImportCheckpoint { Mode = modeKey };
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            GaccagImportCheckpoint? checkpoint =
                await JsonSerializer.DeserializeAsync<GaccagImportCheckpoint>(
                    stream,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken);

            return checkpoint is not null
                   && string.Equals(checkpoint.Mode, modeKey, StringComparison.Ordinal)
                ? checkpoint
                : new GaccagImportCheckpoint { Mode = modeKey };
        }
        catch
        {
            return new GaccagImportCheckpoint { Mode = modeKey };
        }
    }

    private static bool IsDictionaryKana(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            bool isHiragana = value is >= 0x3041 and <= 0x3096;
            bool isLongSoundMark = value == 0x30FC;
            if (!isHiragana && !isLongSoundMark)
            {
                return false;
            }
        }

        return true;
    }

    private async Task SaveSafelyAsync<T>(
        string destinationPath,
        T value,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = destinationPath + ".tmp";
        string backupPath = destinationPath + ".backup";

        await using (var fileStream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            if (destinationPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var gzipStream = new GZipStream(
                    fileStream,
                    CompressionLevel.Fastest,
                    leaveOpen: true);
                await JsonSerializer.SerializeAsync(
                    gzipStream,
                    value,
                    _writeOptions,
                    cancellationToken);
            }
            else
            {
                await JsonSerializer.SerializeAsync(
                    fileStream,
                    value,
                    _writeOptions,
                    cancellationToken);
            }
        }

        if (File.Exists(destinationPath))
        {
            File.Copy(destinationPath, backupPath, overwrite: true);
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static string? FindEdgeExecutable()
    {
        string[] candidates =
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed class EdgeDevToolsBrowser : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly string _userDataDirectory;
        private readonly ClientWebSocket _socket;
        private int _nextCommandId;

        private EdgeDevToolsBrowser(
            Process process,
            string userDataDirectory,
            ClientWebSocket socket)
        {
            _process = process;
            _userDataDirectory = userDataDirectory;
            _socket = socket;
        }

        public static async Task<EdgeDevToolsBrowser> StartAsync(
            string edgePath,
            CancellationToken cancellationToken)
        {
            string userDataDirectory = Path.Combine(
                Path.GetTempPath(),
                "KotodamanWordFinder",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDataDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = edgePath,
                Arguments =
                    $"--headless=new --disable-gpu --no-first-run --no-default-browser-check " +
                    $"--disable-background-networking --disable-features=msEdgeFirstRunExperience --remote-allow-origins=* " +
                    $"--remote-debugging-port=0 --user-data-dir=\"{userDataDirectory}\" about:blank",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Microsoft Edge를 실행하지 못했습니다.");

            try
            {
                int port = await WaitForDevToolsPortAsync(
                    userDataDirectory,
                    process,
                    cancellationToken);
                Uri pageWebSocketUri = await WaitForPageWebSocketAsync(
                    port,
                    process,
                    cancellationToken);

                var socket = new ClientWebSocket();
                await socket.ConnectAsync(pageWebSocketUri, cancellationToken);

                var browser = new EdgeDevToolsBrowser(
                    process,
                    userDataDirectory,
                    socket);
                await browser.SendCommandAsync("Page.enable", null, cancellationToken);
                await browser.SendCommandAsync("Runtime.enable", null, cancellationToken);
                return browser;
            }
            catch
            {
                TryKillProcess(process);
                TryDeleteDirectory(userDataDirectory);
                throw;
            }
        }

        public async Task NavigateAsync(string url, CancellationToken cancellationToken)
        {
            await SendCommandAsync(
                "Page.navigate",
                new { url },
                cancellationToken);
        }

        public async Task<string> WaitForRenderedHtmlAsync(CancellationToken cancellationToken)
        {
            int lastRowCount = -1;
            int stableCount = 0;
            bool pageCompleted = false;

            // 단순 readyState만 기다리면 지연 렌더링 테이블이 덜 채워진 상태일 수 있습니다.
            // 반복 중에는 행 수만 확인하고, 충분히 안정된 뒤 최종 DOM을 한 번만 가져옵니다.
            for (int attempt = 0; attempt < 120; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    JsonElement value = await EvaluateAsync(
                        "(() => { window.scrollTo(0, document.body?.scrollHeight || 0); return ({ ready: document.readyState, rows: document.querySelectorAll('table tr').length }); })()",
                        cancellationToken);

                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        string ready = value.TryGetProperty("ready", out JsonElement readyElement)
                            ? readyElement.GetString() ?? string.Empty
                            : string.Empty;
                        int rows = value.TryGetProperty("rows", out JsonElement rowsElement) && rowsElement.TryGetInt32(out int rowCount)
                            ? rowCount
                            : 0;

                        pageCompleted |= ready == "complete";
                        if (ready == "complete" && rows > 0)
                        {
                            if (rows == lastRowCount)
                            {
                                stableCount++;
                            }
                            else
                            {
                                lastRowCount = rows;
                                stableCount = 0;
                            }

                            // 약 1초 동안 행 수가 변하지 않으면 동적 목록이 채워졌다고 봅니다.
                            if (stableCount >= 4)
                            {
                                JsonElement htmlValue = await EvaluateAsync(
                                    "document.documentElement?.outerHTML || ''",
                                    cancellationToken);
                                string html = htmlValue.ValueKind == JsonValueKind.String
                                    ? htmlValue.GetString() ?? string.Empty
                                    : string.Empty;
                                if (html.Length > 0)
                                {
                                    return html;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // 페이지 이동 직후 실행 컨텍스트가 바뀌는 순간은 재시도합니다.
                }

                await Task.Delay(250, cancellationToken);
            }

            // 표 감지가 늦었더라도 페이지 자체가 로드됐다면 마지막 DOM을 한 번 시도합니다.
            if (pageCompleted)
            {
                JsonElement htmlValue = await EvaluateAsync(
                    "document.documentElement?.outerHTML || ''",
                    cancellationToken);
                string html = htmlValue.ValueKind == JsonValueKind.String
                    ? htmlValue.GetString() ?? string.Empty
                    : string.Empty;
                if (html.Length > 0)
                {
                    return html;
                }
            }

            throw new TimeoutException("Edge에서 렌더링된 페이지 HTML을 가져오지 못했습니다.");
        }

        public async Task WaitForDictionaryPageAsync(CancellationToken cancellationToken)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    JsonElement value = await EvaluateAsync(
                        "(() => ({ ready: document.readyState, hasInput: !![...document.querySelectorAll('input')].find(x => (x.placeholder || '').includes('ひらがな')) }))()",
                        cancellationToken);

                    if (value.ValueKind == JsonValueKind.Object
                        && value.TryGetProperty("ready", out JsonElement ready)
                        && value.TryGetProperty("hasInput", out JsonElement hasInput)
                        && ready.GetString() == "complete"
                        && hasInput.GetBoolean())
                    {
                        return;
                    }
                }
                catch
                {
                    // 페이지 이동 중에는 실행 컨텍스트가 잠깐 사라질 수 있습니다.
                }

                await Task.Delay(150, cancellationToken);
            }

            throw new TimeoutException(
                "GACCAG 사전 페이지가 열렸지만 검색 화면을 확인하지 못했습니다.");
        }

        public async Task<IReadOnlyList<GaccagRow>> TryExtractCompleteDictionaryAsync(
            CancellationToken cancellationToken)
        {
            // DOM, DataTables, window 전역값, 저장소뿐 아니라 현재 페이지가 불러온
            // 같은 출처의 JS/JSON 응답까지 훑습니다. 사이트가 정적 사전 파일을
            // 사용하는 경우 수만 건을 검색 없이 한 번에 가져올 수 있습니다.
            const string extractionScript = """
                (async () => {
                    const textContainer = document.createElement('div');
                    const clean = value => {
                        textContainer.innerHTML = String(value ?? '');
                        return (textContainer.innerText || textContainer.textContent || '')
                            .replace(/\s+/g, ' ')
                            .trim();
                    };
                    const rows = [];
                    const seen = new Set();

                    const add = (kanaValue, requiredValue, lengthValue, displayValue) => {
                        const kana = clean(kanaValue);
                        const required = clean(requiredValue);
                        const display = clean(displayValue);
                        const match = clean(lengthValue).match(/\d+/);
                        if (!kana || !match || !/^[ぁ-ゖー]+$/.test(kana)) return;

                        const length = Number(match[0]);
                        if (length < 2 || length > 7) return;

                        const key = `${kana}\u0001${display}\u0001${length}`;
                        if (seen.has(key)) return;
                        seen.add(key);
                        rows.push({ kana, required, length, display });
                    };

                    const addFromNode = node => {
                        if (!node?.querySelectorAll) return;
                        const cells = [...node.querySelectorAll('td')];
                        if (cells.length < 4) return;
                        add(cells[0].innerHTML, cells[1].innerHTML,
                            cells[2].innerHTML, cells[3].innerHTML);
                    };

                    const inspectArray = value => {
                        if (!Array.isArray(value) || value.length < 100) return;
                        for (const item of value) {
                            if (Array.isArray(item) && item.length >= 4) {
                                add(item[0], item[1], item[2], item[3]);
                            } else if (item && typeof item === 'object') {
                                const kana = item.kana ?? item.yomi ?? item.reading ?? item.word;
                                const required = item.required ?? item.need ?? item.other ?? '';
                                const length = item.length ?? item.count ?? item.size;
                                const display = item.display ?? item.name ?? item.label ?? kana;
                                if (kana != null && length != null) {
                                    add(kana, required, length, display);
                                }
                            }
                        }
                    };

                    const inspectContainer = value => {
                        inspectArray(value);
                        if (!value || typeof value !== 'object' || Array.isArray(value)) return;
                        try {
                            for (const child of Object.values(value).slice(0, 500)) {
                                inspectArray(child);
                            }
                        } catch {}
                    };

                    const inspectText = source => {
                        if (!source || source.length < 1000) return;

                        // HTML 표 형태
                        const htmlPattern = /<td[^>]*>\s*([ぁ-ゖー]{2,7})\s*<\/td>[\s\S]{0,400}?<td[^>]*>([\s\S]*?)<\/td>[\s\S]{0,300}?<td[^>]*>\s*([2-7])(?:文字)?\s*<\/td>[\s\S]{0,400}?<td[^>]*>([\s\S]*?)<\/td>/g;
                        for (const match of source.matchAll(htmlPattern)) {
                            add(match[1], match[2], match[3], match[4]);
                        }

                        // 흔한 JSON/JavaScript 배열 형태: [かな, 必要他字, 字数, 言葉]
                        const arrayPattern = /[\[,(]\s*["']([ぁ-ゖー]{2,7})["']\s*,\s*["']([^"']*)["']\s*,\s*["']?([2-7])(?:文字)?["']?\s*,\s*["']([^"']*)["']/g;
                        for (const match of source.matchAll(arrayPattern)) {
                            add(match[1], match[2], match[3], match[4]);
                        }

                        // 객체 형태
                        const objectPattern = /(?:kana|yomi|reading)\s*[:=]\s*["']([ぁ-ゖー]{2,7})["'][\s\S]{0,250}?(?:length|count|size)\s*[:=]\s*["']?([2-7])["']?[\s\S]{0,250}?(?:display|name|label)\s*[:=]\s*["']([^"']*)["']/g;
                        for (const match of source.matchAll(objectPattern)) {
                            add(match[1], '', match[2], match[3]);
                        }
                    };

                    for (const row of document.querySelectorAll('table tr')) addFromNode(row);

                    try {
                        const jq = window.jQuery;
                        if (jq?.fn?.dataTable) {
                            for (const table of document.querySelectorAll('table')) {
                                if (!jq.fn.dataTable.isDataTable(table)) continue;
                                const data = jq(table).DataTable().rows().data().toArray();
                                inspectArray(data);
                            }
                        }
                    } catch {}

                    for (const name of Object.getOwnPropertyNames(window)) {
                        try { inspectContainer(window[name]); } catch {}
                    }

                    for (const storage of [window.localStorage, window.sessionStorage]) {
                        try {
                            for (let index = 0; index < storage.length; index++) {
                                const raw = storage.getItem(storage.key(index));
                                if (!raw || raw.length < 1000) continue;
                                inspectText(raw);
                                try { inspectContainer(JSON.parse(raw)); } catch {}
                            }
                        } catch {}
                    }

                    for (const script of document.querySelectorAll('script:not([src])')) {
                        inspectText(script.textContent || '');
                    }

                    const resourceUrls = performance.getEntriesByType('resource')
                        .filter(entry => ['script', 'fetch', 'xmlhttprequest'].includes(entry.initiatorType))
                        .map(entry => entry.name);
                    const urls = [...new Set(
                        resourceUrls.concat(
                            [...document.querySelectorAll('script[src]')].map(script => script.src))
                    )].filter(url => {
                        try {
                            const parsed = new URL(url, location.href);
                            return parsed.origin === location.origin;
                        } catch { return false; }
                    }).slice(0, 100);

                    for (const url of urls) {
                        try {
                            const controller = new AbortController();
                            const timer = setTimeout(() => controller.abort(), 8000);
                            const response = await fetch(url, {
                                cache: 'force-cache',
                                signal: controller.signal
                            });
                            clearTimeout(timer);
                            const length = Number(response.headers.get('content-length') || 0);
                            if (!response.ok || length > 50_000_000) continue;
                            const source = await response.text();
                            if (source.length <= 50_000_000) inspectText(source);
                        } catch {}
                    }

                    return JSON.stringify(rows);
                })()
                """;

            try
            {
                JsonElement value = await EvaluateAsync(extractionScript, cancellationToken);
                string json = value.GetString() ?? "[]";

                return JsonSerializer.Deserialize<List<GaccagRow>>(
                           json,
                           new JsonSerializerOptions
                           {
                               PropertyNameCaseInsensitive = true
                           })
                       ?? new List<GaccagRow>();
            }
            catch
            {
                return Array.Empty<GaccagRow>();
            }
        }

        public async Task<bool> CanUseFastBatchSearchAsync(
            int wordLength,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<SearchPageResult> probe = await SearchBatchAsync(
                new[] { (Length: wordLength, Prefix: "あ") },
                cancellationToken);

            return probe.Count == 1
                   && (probe[0].QueryMatches || probe[0].Rows.Count > 0);
        }

        public async Task<IReadOnlyList<SearchPageResult>> SearchBatchAsync(
            IReadOnlyList<(int Length, string Prefix)> queries,
            CancellationToken cancellationToken)
        {
            if (queries.Count == 0)
            {
                return Array.Empty<SearchPageResult>();
            }

            string requestsJson = JsonSerializer.Serialize(
                queries.Select(query => new
                {
                    query = query.Prefix,
                    wordLength = query.Length
                }));

            string extractionScript = $$"""
                (async () => {
                    const requests = {{requestsJson}};
                    const compact = value => (value || '').replace(/\s+/g, '');
                    const text = element => (element?.innerText || element?.textContent || '').trim();
                    const associatedText = input => {
                        const parentLabel = input.closest('label');
                        if (parentLabel) return compact(parentLabel.textContent);
                        if (input.id) {
                            const label = document.querySelector(`label[for="${CSS.escape(input.id)}"]`);
                            if (label) return compact(label.textContent);
                        }
                        return compact(input.parentElement?.textContent);
                    };

                    const textInput = [...document.querySelectorAll('input')]
                        .find(input => (input.placeholder || '').includes('ひらがな'));
                    const form = textInput?.form || textInput?.closest('form');
                    if (!textInput || !form || !textInput.name) {
                        return JSON.stringify({
                            ok: false,
                            error: '검색 폼 또는 검색어 필드 이름을 찾지 못했습니다.',
                            results: []
                        });
                    }

                    const startRadio = [...document.querySelectorAll('input[type="radio"]')]
                        .find(input => associatedText(input).includes('で始まる'));
                    const fuzzyCheck = [...document.querySelectorAll('input[type="checkbox"]')]
                        .find(input => associatedText(input).includes('濁音・拗音等も含める'));
                    const lengthSelect = [...document.querySelectorAll('select')]
                        .find(select => [...select.options]
                            .some(option => compact(option.textContent).includes('7文字')));
                    const searchButton = [...document.querySelectorAll(
                            'button, input[type="submit"], input[type="button"]')]
                        .find(element => compact(element.innerText || element.value) === '検索');

                    if (!startRadio || !lengthSelect) {
                        return JSON.stringify({
                            ok: false,
                            error: '시작 문자 또는 글자 수 검색 옵션을 찾지 못했습니다.',
                            results: []
                        });
                    }

                    const original = {
                        text: textInput.value,
                        startChecked: startRadio.checked,
                        fuzzyChecked: fuzzyCheck?.checked ?? false,
                        lengthValue: lengthSelect.value
                    };

                    const descriptors = [];
                    try {
                        for (const request of requests) {
                            const option = [...lengthSelect.options]
                                .find(item => compact(item.textContent)
                                    .includes(`${request.wordLength}文字`));
                            if (!option) {
                                descriptors.push({
                                    query: request.query,
                                    wordLength: request.wordLength,
                                    error: `${request.wordLength}글자 선택 항목을 찾지 못했습니다.`,
                                    entries: []
                                });
                                continue;
                            }

                            textInput.value = request.query;
                            startRadio.checked = true;
                            if (fuzzyCheck) fuzzyCheck.checked = false;
                            lengthSelect.value = option.value;
                            option.selected = true;

                            let formData;
                            try {
                                formData = searchButton
                                    ? new FormData(form, searchButton)
                                    : new FormData(form);
                            } catch {
                                formData = new FormData(form);
                            }

                            formData.set(textInput.name, request.query);
                            if (startRadio.name) {
                                formData.set(startRadio.name, startRadio.value || 'on');
                            }
                            if (fuzzyCheck?.name) {
                                formData.delete(fuzzyCheck.name);
                            }
                            if (lengthSelect.name) {
                                formData.set(lengthSelect.name, option.value);
                            }
                            if (searchButton?.name) {
                                formData.set(
                                    searchButton.name,
                                    searchButton.value || text(searchButton) || '検索');
                            }

                            descriptors.push({
                                query: request.query,
                                wordLength: request.wordLength,
                                error: '',
                                entries: [...formData.entries()]
                                    .map(([key, value]) => [key, String(value)])
                            });
                        }
                    } finally {
                        textInput.value = original.text;
                        startRadio.checked = original.startChecked;
                        if (fuzzyCheck) fuzzyCheck.checked = original.fuzzyChecked;
                        lengthSelect.value = original.lengthValue;
                    }

                    const parseRows = (html, descriptor) => {
                        const doc = new DOMParser().parseFromString(html, 'text/html');
                        const rows = [];

                        for (const row of doc.querySelectorAll('table tr')) {
                            const cells = [...row.querySelectorAll('td')];
                            if (cells.length < 4) continue;

                            const kana = text(cells[0]);
                            const required = text(cells[1]);
                            const lengthText = text(cells[2]);
                            const display = text(cells[3]);
                            const lengthMatch = lengthText.match(/\d+/);
                            if (!kana || !lengthMatch || !/^[ぁ-ゖー]+$/.test(kana)) continue;

                            rows.push({
                                kana,
                                required,
                                length: Number(lengthMatch[0]),
                                display
                            });
                        }

                        const responseInput = [...doc.querySelectorAll('input')]
                            .find(input => (input.placeholder || '').includes('ひらがな'));
                        const responseLength = [...doc.querySelectorAll('select option:checked')]
                            .some(option => compact(option.textContent)
                                .includes(`${descriptor.wordLength}文字`));
                        const queryMatches = rows.length > 0
                            || ((responseInput?.value || '') === descriptor.query && responseLength);

                        return { rows, queryMatches };
                    };

                    const execute = async descriptor => {
                        if (descriptor.error) {
                            return {
                                query: descriptor.query,
                                wordLength: descriptor.wordLength,
                                queryMatches: false,
                                error: descriptor.error,
                                rows: []
                            };
                        }

                        let lastError = '';
                        for (let attempt = 0; attempt < 3; attempt++) {
                            try {
                                const method = (form.method || 'GET').toUpperCase();
                                const action = new URL(form.action || location.href, location.href);
                                const options = {
                                    method,
                                    credentials: 'include',
                                    redirect: 'follow',
                                    cache: 'no-store',
                                    headers: {
                                        'Accept': 'text/html,application/xhtml+xml'
                                    }
                                };

                                if (method === 'GET') {
                                    for (const [key, value] of descriptor.entries) {
                                        action.searchParams.append(key, value);
                                    }
                                } else if ((form.enctype || '').toLowerCase()
                                    .includes('multipart/form-data')) {
                                    const body = new FormData();
                                    for (const [key, value] of descriptor.entries) {
                                        body.append(key, value);
                                    }
                                    options.body = body;
                                } else {
                                    options.headers['Content-Type'] =
                                        'application/x-www-form-urlencoded;charset=UTF-8';
                                    options.body = new URLSearchParams(descriptor.entries);
                                }

                                const response = await fetch(action.href, options);
                                if (!response.ok) {
                                    throw new Error(`HTTP ${response.status}`);
                                }

                                const html = await response.text();
                                const parsed = parseRows(html, descriptor);
                                if (!parsed.queryMatches) {
                                    throw new Error('검색 조건이 응답에 적용되지 않았습니다.');
                                }

                                return {
                                    query: descriptor.query,
                                    wordLength: descriptor.wordLength,
                                    queryMatches: true,
                                    error: '',
                                    rows: parsed.rows
                                };
                            } catch (error) {
                                lastError = String(error?.message || error || '직접 요청 실패');
                                if (attempt < 2) {
                                    await new Promise(resolve => setTimeout(resolve, 250 * (attempt + 1)));
                                }
                            }
                        }

                        return {
                            query: descriptor.query,
                            wordLength: descriptor.wordLength,
                            queryMatches: false,
                            error: lastError,
                            rows: []
                        };
                    };

                    const results = new Array(descriptors.length);
                    let nextIndex = 0;
                    const concurrency = Math.min(8, Math.max(1, descriptors.length));
                    const workers = Array.from({ length: concurrency }, async () => {
                        while (true) {
                            const index = nextIndex++;
                            if (index >= descriptors.length) return;
                            results[index] = await execute(descriptors[index]);
                        }
                    });
                    await Promise.all(workers);

                    return JSON.stringify({ ok: true, error: '', results });
                })()
                """;

            JsonElement value = await EvaluateAsync(extractionScript, cancellationToken);
            string json = value.GetString()
                ?? throw new InvalidOperationException("GACCAG 직접 요청 결과를 읽지 못했습니다.");

            FastBatchEnvelope envelope = JsonSerializer.Deserialize<FastBatchEnvelope>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("GACCAG 직접 요청 결과 형식이 올바르지 않습니다.");

            if (!envelope.Ok)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(envelope.Error)
                        ? "GACCAG 직접 요청 모드를 준비하지 못했습니다."
                        : envelope.Error);
            }

            if (envelope.Results.Any(result => !string.IsNullOrWhiteSpace(result.Error)))
            {
                FastBatchItem failed = envelope.Results.First(result =>
                    !string.IsNullOrWhiteSpace(result.Error));
                throw new InvalidOperationException(
                    $"'{failed.Query}' 직접 검색 실패: {failed.Error}");
            }

            return envelope.Results
                .Select(result => new SearchPageResult
                {
                    QueryMatches = result.QueryMatches,
                    ExpectedLength = result.WordLength,
                    Rows = result.Rows
                })
                .ToArray();
        }

        public async Task<SearchPageResult> SearchAsync(
            string query,
            int wordLength,
            CancellationToken cancellationToken)
        {
            string queryJson = JsonSerializer.Serialize(query);
            string setupScript = $$"""
                (async () => {
                    const compact = value => (value || '').replace(/\s+/g, '');
                    const dispatch = element => {
                        element.dispatchEvent(new Event('input', { bubbles: true }));
                        element.dispatchEvent(new Event('change', { bubbles: true }));
                    };
                    const associatedText = input => {
                        const parentLabel = input.closest('label');
                        if (parentLabel) return compact(parentLabel.textContent);
                        if (input.id) {
                            const label = document.querySelector(`label[for="${CSS.escape(input.id)}"]`);
                            if (label) return compact(label.textContent);
                        }
                        return compact(input.parentElement?.textContent);
                    };

                    const textInput = [...document.querySelectorAll('input')]
                        .find(input => (input.placeholder || '').includes('ひらがな'));
                    if (!textInput) return { ok: false, error: '검색 입력창을 찾지 못했습니다.' };

                    const startRadio = [...document.querySelectorAll('input[type="radio"]')]
                        .find(input => associatedText(input).includes('で始まる'));
                    if (!startRadio) return { ok: false, error: '시작 문자 검색 옵션을 찾지 못했습니다.' };
                    startRadio.checked = true;
                    startRadio.click();
                    dispatch(startRadio);

                    const fuzzyCheck = [...document.querySelectorAll('input[type="checkbox"]')]
                        .find(input => associatedText(input).includes('濁音・拗音等も含める'));
                    if (fuzzyCheck && fuzzyCheck.checked) {
                        fuzzyCheck.checked = false;
                        dispatch(fuzzyCheck);
                    }

                    const lengthSelect = [...document.querySelectorAll('select')]
                        .find(select => [...select.options]
                            .some(option => compact(option.textContent).includes('{{wordLength}}文字')));
                    if (!lengthSelect) return { ok: false, error: '글자 수 선택 상자를 찾지 못했습니다.' };

                    const option = [...lengthSelect.options]
                        .find(item => compact(item.textContent).includes('{{wordLength}}文字'));
                    if (!option) return { ok: false, error: '{{wordLength}}글자 선택 항목을 찾지 못했습니다.' };

                    lengthSelect.value = option.value;
                    option.selected = true;
                    dispatch(lengthSelect);

                    const valueSetter = Object.getOwnPropertyDescriptor(
                        HTMLInputElement.prototype,
                        'value')?.set;
                    if (valueSetter) valueSetter.call(textInput, {{queryJson}});
                    else textInput.value = {{queryJson}};
                    dispatch(textInput);

                    await new Promise(resolve =>
                        requestAnimationFrame(() => requestAnimationFrame(resolve)));

                    const searchButton = [...document.querySelectorAll('button, input[type="button"], input[type="submit"]')]
                        .find(element => compact(element.innerText || element.value) === '検索');
                    if (!searchButton) return { ok: false, error: '검색 버튼을 찾지 못했습니다.' };

                    searchButton.click();
                    return { ok: true };
                })()
                """;

            JsonElement setupResult = await EvaluateAsync(setupScript, cancellationToken);
            if (setupResult.ValueKind != JsonValueKind.Object
                || !setupResult.TryGetProperty("ok", out JsonElement ok)
                || !ok.GetBoolean())
            {
                string error = setupResult.ValueKind == JsonValueKind.Object
                               && setupResult.TryGetProperty("error", out JsonElement errorElement)
                    ? errorElement.GetString() ?? "알 수 없는 화면 분석 오류"
                    : "GACCAG 검색 화면을 조작하지 못했습니다.";
                throw new InvalidOperationException(error);
            }

            string? previousSignature = null;
            int stableCount = 0;

            for (int attempt = 0; attempt < 80; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(80, cancellationToken);

                try
                {
                    SearchPageResult result = await ExtractRowsAsync(
                        query,
                        wordLength,
                        cancellationToken);

                    string signature = BuildSignature(result.Rows);
                    if (string.Equals(signature, previousSignature, StringComparison.Ordinal))
                    {
                        stableCount++;
                    }
                    else
                    {
                        stableCount = 0;
                        previousSignature = signature;
                    }

                    if (result.QueryMatches && stableCount >= 1 && attempt >= 2)
                    {
                        return result;
                    }

                    if (result.QueryMatches && result.Rows.Count == 0 && attempt >= 5)
                    {
                        return result;
                    }
                }
                catch
                {
                    // 폼 제출로 페이지가 다시 로드되면 잠시 실행 컨텍스트가 사라질 수 있습니다.
                }
            }

            throw new TimeoutException(
                $"GACCAG에서 '{query}' {wordLength}글자 검색 결과를 기다리다 시간이 초과되었습니다.");
        }

        private async Task<SearchPageResult> ExtractRowsAsync(
            string expectedQuery,
            int expectedLength,
            CancellationToken cancellationToken)
        {
            string expectedQueryJson = JsonSerializer.Serialize(expectedQuery);
            string extractionScript = $$"""
                (() => {
                    const text = element => (element?.innerText || element?.textContent || '').trim();
                    const input = [...document.querySelectorAll('input')]
                        .find(item => (item.placeholder || '').includes('ひらがな'));
                    const rows = [];

                    for (const row of document.querySelectorAll('table tr')) {
                        const style = getComputedStyle(row);
                        if (style.display === 'none' || style.visibility === 'hidden') continue;

                        const cells = [...row.querySelectorAll('td')];
                        if (cells.length < 4) continue;

                        const kana = text(cells[0]);
                        const required = text(cells[1]);
                        const lengthText = text(cells[2]);
                        const display = text(cells[3]);
                        const lengthMatch = lengthText.match(/\d+/);
                        if (!kana || !lengthMatch) continue;

                        rows.push({
                            kana,
                            required,
                            length: Number(lengthMatch[0]),
                            display
                        });
                    }

                    return JSON.stringify({
                        queryMatches: (input?.value || '') === {{expectedQueryJson}},
                        expectedLength: {{expectedLength}},
                        rows
                    });
                })()
                """;

            JsonElement value = await EvaluateAsync(extractionScript, cancellationToken);
            string json = value.GetString()
                ?? throw new InvalidOperationException("GACCAG 검색 결과를 읽지 못했습니다.");

            return JsonSerializer.Deserialize<SearchPageResult>(
                       json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new SearchPageResult();
        }

        private static string BuildSignature(IReadOnlyList<GaccagRow> rows)
        {
            if (rows.Count == 0)
            {
                return "0";
            }

            return $"{rows.Count}|{rows[0].Kana}|{rows[Math.Min(1, rows.Count - 1)].Kana}|{rows[^1].Kana}";
        }

        private async Task<JsonElement> EvaluateAsync(
            string expression,
            CancellationToken cancellationToken)
        {
            JsonElement result = await SendCommandAsync(
                "Runtime.evaluate",
                new
                {
                    expression,
                    awaitPromise = true,
                    returnByValue = true
                },
                cancellationToken);

            if (result.TryGetProperty("exceptionDetails", out JsonElement exceptionDetails))
            {
                string description = exceptionDetails.TryGetProperty("text", out JsonElement text)
                    ? text.GetString() ?? "JavaScript 실행 오류"
                    : "JavaScript 실행 오류";
                throw new InvalidOperationException(description);
            }

            if (!result.TryGetProperty("result", out JsonElement remoteObject))
            {
                throw new InvalidOperationException("Edge에서 실행 결과를 받지 못했습니다.");
            }

            if (remoteObject.TryGetProperty("value", out JsonElement value))
            {
                return value.Clone();
            }

            return default;
        }

        private async Task<JsonElement> SendCommandAsync(
            string method,
            object? parameters,
            CancellationToken cancellationToken)
        {
            int id = Interlocked.Increment(ref _nextCommandId);
            var command = new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method
            };
            if (parameters is not null)
            {
                command["params"] = parameters;
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(command);

            await _socket.SendAsync(
                new ArraySegment<byte>(payload),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);

            while (true)
            {
                string message = await ReceiveMessageAsync(cancellationToken);
                using JsonDocument document = JsonDocument.Parse(message);
                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("id", out JsonElement responseId)
                    || responseId.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out JsonElement error))
                {
                    string errorMessage = error.TryGetProperty("message", out JsonElement errorText)
                        ? errorText.GetString() ?? "Edge DevTools 오류"
                        : "Edge DevTools 오류";
                    throw new InvalidOperationException(errorMessage);
                }

                return root.TryGetProperty("result", out JsonElement result)
                    ? result.Clone()
                    : default;
            }
        }

        private async Task<string> ReceiveMessageAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            using var stream = new MemoryStream();

            while (true)
            {
                WebSocketReceiveResult receiveResult = await _socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("Microsoft Edge와의 연결이 종료되었습니다.");
                }

                stream.Write(buffer, 0, receiveResult.Count);
                if (receiveResult.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "done",
                        CancellationToken.None);
                }
            }
            catch
            {
                // 종료 중 연결 오류는 무시합니다.
            }

            _socket.Dispose();
            TryKillProcess(_process);
            TryDeleteDirectory(_userDataDirectory);
        }

        private static async Task<int> WaitForDevToolsPortAsync(
            string userDataDirectory,
            Process process,
            CancellationToken cancellationToken)
        {
            string portFilePath = Path.Combine(userDataDirectory, "DevToolsActivePort");

            for (int attempt = 0; attempt < 100; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureProcessRunning(process);

                if (File.Exists(portFilePath))
                {
                    string[] lines = await File.ReadAllLinesAsync(
                        portFilePath,
                        cancellationToken);
                    if (lines.Length > 0 && int.TryParse(lines[0], out int port))
                    {
                        return port;
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException("Microsoft Edge의 자동화 연결 포트를 찾지 못했습니다.");
        }

        private static async Task<Uri> WaitForPageWebSocketAsync(
            int port,
            Process process,
            CancellationToken cancellationToken)
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(3)
            };

            for (int attempt = 0; attempt < 100; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureProcessRunning(process);

                try
                {
                    string json = await httpClient.GetStringAsync(
                        $"http://127.0.0.1:{port}/json/list",
                        cancellationToken);
                    using JsonDocument document = JsonDocument.Parse(json);

                    foreach (JsonElement target in document.RootElement.EnumerateArray())
                    {
                        if (target.TryGetProperty("type", out JsonElement type)
                            && type.GetString() == "page"
                            && target.TryGetProperty(
                                "webSocketDebuggerUrl",
                                out JsonElement webSocketUrl)
                            && Uri.TryCreate(
                                webSocketUrl.GetString(),
                                UriKind.Absolute,
                                out Uri? uri))
                        {
                            return uri;
                        }
                    }
                }
                catch
                {
                    // Edge가 디버깅 서버를 준비하는 중일 수 있습니다.
                }

                await Task.Delay(100, cancellationToken);
            }

            throw new TimeoutException("Microsoft Edge의 페이지 자동화 연결을 만들지 못했습니다.");
        }

        private static void EnsureProcessRunning(Process process)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Microsoft Edge가 예기치 않게 종료되었습니다. 종료 코드: {process.ExitCode}");
            }
        }
    }

    private sealed class FastBatchEnvelope
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = string.Empty;
        public List<FastBatchItem> Results { get; set; } = new();
    }

    private sealed class FastBatchItem
    {
        public string Query { get; set; } = string.Empty;
        public int WordLength { get; set; }
        public bool QueryMatches { get; set; }
        public string Error { get; set; } = string.Empty;
        public List<GaccagRow> Rows { get; set; } = new();
    }

    private sealed class SearchPageResult
    {
        public bool QueryMatches { get; set; }
        public int ExpectedLength { get; set; }
        public List<GaccagRow> Rows { get; set; } = new();
    }

    private sealed class GaccagRow
    {
        public string Kana { get; set; } = string.Empty;
        public string Required { get; set; } = string.Empty;
        public int Length { get; set; }
        public string Display { get; set; } = string.Empty;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch
        {
            // 임시 브라우저 종료 실패가 앱 종료를 막지 않도록 합니다.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Edge가 파일 핸들을 늦게 놓는 경우 임시 폴더가 남을 수 있습니다.
        }
    }
}
