using System.IO;
using System.Runtime.InteropServices;
using Int32Rect = System.Windows.Int32Rect;
using System.Windows.Media.Imaging;
using KotodamanWordFinder.Models;
using OpenCvSharp;

namespace KotodamanWordFinder.Services;

/// <summary>
/// 코토다망 덱 스크린샷의 4x3 슬롯을 캐릭터 DB 이미지와 비교합니다.
/// 대규모 DB에서도 기다리는 시간을 줄이기 위해 12개 슬롯 병렬 비교 + ORB 특징 영구 캐시를 사용합니다.
/// v1.21.1에서는 검수 확정 슬롯을 UI 프로필별 학습 샘플로 재사용하고, 상위 후보 reciprocal cross-check를 함께 사용합니다.
/// </summary>
public sealed class DeckScreenshotRecognitionService : IDisposable
{
    public const int ColumnCount = 4;
    public const int RowCount = 3;
    public const int SlotCount = ColumnCount * RowCount;

    private const double RatioTestThreshold = 0.78;
    private const int OrbFeatureCount = 600;
    private const int PersistentCacheVersion = 4;
    private const int PersistentCacheMagic = 0x4B574F52; // KWOR
    private const int FullRefineCandidateCount = 220;
    private const int ReciprocalRefineCandidateCount = 12;
    private const float ReciprocalDistanceThreshold = 64f;
    private const int AttributeHistogramBins = 36;
    private const double MinimumAttributeAssistConfidence = 0.45;

    private static readonly RelativeRect[] SlotPortraitRegions =
    {
        new(0.16, 0.02, 0.90, 0.66),
        new(0.22, 0.04, 0.88, 0.62),
        new(0.10, 0.00, 0.95, 0.72)
    };

    private readonly string _dataDirectory;
    private readonly IReadOnlyList<CharacterEntry> _characters;
    private readonly DeckScreenshotLearningService _learningService;
    private readonly Dictionary<string, Mat?> _templateDescriptorCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedDescriptorData> _persistentDescriptorCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedPersistentCacheKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _templateBuildLock = new();
    private readonly string _persistentCachePath;
    private List<TemplateEntry>? _baseTemplates;
    private List<TemplateEntry>? _learnedTemplates;
    private IReadOnlyDictionary<string, AttributeColorProfile>? _learnedAttributeProfiles;
    private bool _persistentCacheDirty;
    private bool _disposed;

    public DeckScreenshotRecognitionService(
        string dataDirectory,
        IReadOnlyList<CharacterEntry> characters)
    {
        _dataDirectory = dataDirectory;
        _characters = characters;
        _learningService = new DeckScreenshotLearningService(dataDirectory);
        _persistentCachePath = GetPersistentCachePath();
        LoadPersistentDescriptorCache();
    }

    public IReadOnlyList<DeckScreenshotSlotMatch> Recognize(
        BitmapSource screenshot,
        Int32Rect gridRect,
        int candidateCount = 3,
        bool useLearnedSamples = true,
        bool useAttributeColorAssist = true)
        => RecognizePrepared(
            PrepareSlots(screenshot, gridRect),
            candidateCount,
            useLearnedSamples,
            useAttributeColorAssist);

    /// <summary>
    /// UI 스레드에서 12칸을 잘라 PNG 바이트로 고정합니다.
    /// 이후 OpenCV 인식 스레드는 WPF 이미지 객체를 읽지 않으므로 Dispatcher 소유권 충돌이 없습니다.
    /// </summary>
    public static IReadOnlyList<PreparedDeckScreenshotSlot> PrepareSlots(
        BitmapSource screenshot,
        Int32Rect gridRect)
    {
        if (gridRect.Width < ColumnCount * 12 || gridRect.Height < RowCount * 12)
        {
            throw new ArgumentException("선택한 덱 영역이 너무 작습니다.", nameof(gridRect));
        }

        var prepared = new PreparedDeckScreenshotSlot[SlotCount];
        for (int row = 0; row < RowCount; row++)
        {
            for (int column = 0; column < ColumnCount; column++)
            {
                int slotIndex = row * ColumnCount + column;
                Int32Rect slotRect = GetSlotRect(gridRect, row, column);
                BitmapSource crop = CropBitmap(screenshot, slotRect);
                prepared[slotIndex] = new PreparedDeckScreenshotSlot(
                    slotIndex,
                    crop,
                    EncodeBitmapToPng(crop));
            }
        }

        return prepared;
    }

    public IReadOnlyList<DeckScreenshotSlotMatch> RecognizePrepared(
        IReadOnlyList<PreparedDeckScreenshotSlot> preparedSlots,
        int candidateCount = 3,
        bool useLearnedSamples = true,
        bool useAttributeColorAssist = true)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DeckScreenshotRecognitionService));
        }

        if (preparedSlots.Count != SlotCount)
        {
            throw new ArgumentException($"덱 슬롯은 정확히 {SlotCount}칸이어야 합니다.", nameof(preparedSlots));
        }

        List<TemplateEntry> templates = GetOrBuildTemplates(useLearnedSamples);
        IReadOnlyDictionary<string, AttributeColorProfile> attributeProfiles =
            useAttributeColorAssist && useLearnedSamples
                ? GetOrBuildLearnedAttributeProfiles()
                : new Dictionary<string, AttributeColorProfile>(StringComparer.Ordinal);
        if (templates.Count == 0)
        {
            throw new InvalidOperationException("비교할 캐릭터 이미지가 없습니다. 캐릭터 DB에 이미지를 먼저 등록하세요.");
        }

        var results = new DeckScreenshotSlotMatch[SlotCount];
        int maxParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

        Parallel.ForEach(
            preparedSlots,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
            workItem =>
            {
                using ORB orb = ORB.Create(OrbFeatureCount);
                using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
                List<Mat> slotDescriptors = BuildSlotDescriptors(workItem.EncodedPng, orb);
                AttributeColorEvidence attributeEvidence = useAttributeColorAssist
                    ? DetectAttributeColor(workItem.EncodedPng, attributeProfiles, workItem.SlotIndex)
                    : AttributeColorEvidence.None;
                try
                {
                    IReadOnlyList<TemplateEntry> refinedTemplates = SelectTemplatesForFullScoring(
                        slotDescriptors,
                        templates,
                        matcher,
                        attributeEvidence);

                    RankedTemplateCandidate[] initialCandidates = refinedTemplates
                        .Select(template => new RankedTemplateCandidate(
                            template,
                            ScoreTemplate(slotDescriptors, template, matcher),
                            0))
                        .OrderByDescending(item =>
                            item.Candidate.MatchCount +
                            GetAttributeRankingAdjustment(item.Candidate.Character, attributeEvidence))
                        .ThenByDescending(item => item.Candidate.MatchCount)
                        .ThenBy(item => item.Candidate.MeanDistance)
                        .ThenBy(item => item.Candidate.Character.Name, StringComparer.Ordinal)
                        .Take(Math.Max(ReciprocalRefineCandidateCount, candidateCount))
                        .ToArray();

                    using var crossCheckMatcher = new BFMatcher(NormTypes.Hamming, crossCheck: true);
                    DeckScreenshotCandidate[] candidates = initialCandidates
                        .Select(item => item with
                        {
                            ReciprocalMatchCount = ScoreTemplateReciprocal(
                                slotDescriptors,
                                item.Template,
                                crossCheckMatcher)
                        })
                        .OrderByDescending(item =>
                            item.Candidate.MatchCount +
                            Math.Min(item.ReciprocalMatchCount, 20) * 0.075 +
                            GetAttributeRankingAdjustment(item.Candidate.Character, attributeEvidence))
                        .ThenByDescending(item => item.Candidate.MatchCount)
                        .ThenByDescending(item => item.ReciprocalMatchCount)
                        .ThenBy(item => item.Candidate.MeanDistance)
                        .ThenBy(item => item.Candidate.Character.Name, StringComparer.Ordinal)
                        .Take(Math.Max(1, candidateCount))
                        .Select(item => item.Candidate)
                        .ToArray();

                    results[workItem.SlotIndex] = new DeckScreenshotSlotMatch(
                        workItem.SlotIndex,
                        workItem.Crop,
                        candidates,
                        attributeEvidence.Attribute,
                        attributeEvidence.Confidence,
                        attributeEvidence.Source);
                }
                finally
                {
                    foreach (Mat descriptor in slotDescriptors)
                    {
                        descriptor.Dispose();
                    }
                }
            });

        return results;
    }


    public void InvalidateLearnedTemplates()
    {
        lock (_templateBuildLock)
        {
            _learnedTemplates = null;
            _learnedAttributeProfiles = null;
        }
    }

    public static Int32Rect GetSlotRect(Int32Rect gridRect, int row, int column)
    {
        int x0 = gridRect.X + (int)Math.Round(gridRect.Width * column / (double)ColumnCount);
        int x1 = gridRect.X + (int)Math.Round(gridRect.Width * (column + 1) / (double)ColumnCount);
        int y0 = gridRect.Y + (int)Math.Round(gridRect.Height * row / (double)RowCount);
        int y1 = gridRect.Y + (int)Math.Round(gridRect.Height * (row + 1) / (double)RowCount);
        return new Int32Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    public static BitmapSource CropBitmap(BitmapSource source, Int32Rect rect)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, source.PixelWidth - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, source.PixelHeight - 1));
        int width = Math.Clamp(rect.Width, 1, source.PixelWidth - x);
        int height = Math.Clamp(rect.Height, 1, source.PixelHeight - y);
        var crop = new CroppedBitmap(source, new Int32Rect(x, y, width, height));
        crop.Freeze();
        return crop;
    }

    private List<TemplateEntry> GetOrBuildTemplates(bool useLearnedSamples)
    {
        List<TemplateEntry>? cached = useLearnedSamples ? _learnedTemplates : _baseTemplates;
        if (cached is not null)
        {
            return cached;
        }

        lock (_templateBuildLock)
        {
            cached = useLearnedSamples ? _learnedTemplates : _baseTemplates;
            if (cached is not null)
            {
                return cached;
            }

            using ORB orb = ORB.Create(OrbFeatureCount);
            _usedPersistentCacheKeys.Clear();
            IReadOnlyDictionary<string, IReadOnlyList<string>> learnedReferences =
                useLearnedSamples
                    ? _learningService.LoadReferenceMap()
                    : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

            var result = new List<TemplateEntry>(_characters.Count);

            foreach (CharacterEntry character in _characters)
            {
                var descriptorSets = new List<Mat>();
                AddTemplateDescriptor(descriptorSets, character.ImageFileName, orb);
                foreach (CharacterForm form in character.AlternateForms ?? new List<CharacterForm>())
                {
                    AddTemplateDescriptor(descriptorSets, form.ImageFileName, orb);
                }

                if (useLearnedSamples &&
                    learnedReferences.TryGetValue(character.Id, out IReadOnlyList<string>? learnedPaths))
                {
                    foreach (string learnedPath in learnedPaths)
                    {
                        AddLearnedTemplateDescriptor(descriptorSets, learnedPath, orb);
                    }
                }

                if (descriptorSets.Count > 0)
                {
                    result.Add(new TemplateEntry(character, descriptorSets));
                }
            }

            PruneAndSavePersistentDescriptorCache();
            if (useLearnedSamples)
            {
                _learnedTemplates = result;
            }
            else
            {
                _baseTemplates = result;
            }

            return result;
        }
    }

    private void AddTemplateDescriptor(List<Mat> target, string? imageFileName, ORB orb)
    {
        if (string.IsNullOrWhiteSpace(imageFileName))
        {
            return;
        }

        string? path = CharacterImageService.ResolveImagePath(_dataDirectory, imageFileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!_templateDescriptorCache.TryGetValue(path, out Mat? descriptor))
        {
            string cacheKey = GetDescriptorCacheKey(path);
            _usedPersistentCacheKeys.Add(cacheKey);

            if (_persistentDescriptorCache.TryGetValue(cacheKey, out CachedDescriptorData? cached) && cached is not null)
            {
                descriptor = CreateDescriptorMat(cached);
            }
            else
            {
                descriptor = BuildTemplateDescriptor(path, orb);
                if (descriptor is not null && !descriptor.Empty() && descriptor.Rows >= 2)
                {
                    _persistentDescriptorCache[cacheKey] = CreateCachedDescriptorData(descriptor);
                    _persistentCacheDirty = true;
                }
            }

            _templateDescriptorCache[path] = descriptor;
        }
        else
        {
            _usedPersistentCacheKeys.Add(GetDescriptorCacheKey(path));
        }

        if (descriptor is not null && !descriptor.Empty() && descriptor.Rows >= 2)
        {
            target.Add(descriptor);
        }
    }

    private void AddLearnedTemplateDescriptor(List<Mat> target, string path, ORB orb)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        string memoryKey = "learned|" + Path.GetFullPath(path);
        if (!_templateDescriptorCache.TryGetValue(memoryKey, out Mat? descriptor))
        {
            string cacheKey = "learned|" + GetDescriptorCacheKey(path);
            _usedPersistentCacheKeys.Add(cacheKey);

            if (_persistentDescriptorCache.TryGetValue(cacheKey, out CachedDescriptorData? cached) &&
                cached is not null)
            {
                descriptor = CreateDescriptorMat(cached);
            }
            else
            {
                descriptor = BuildLearnedTemplateDescriptor(path, orb);
                if (descriptor is not null && !descriptor.Empty() && descriptor.Rows >= 2)
                {
                    _persistentDescriptorCache[cacheKey] = CreateCachedDescriptorData(descriptor);
                    _persistentCacheDirty = true;
                }
            }

            _templateDescriptorCache[memoryKey] = descriptor;
        }
        else
        {
            _usedPersistentCacheKeys.Add("learned|" + GetDescriptorCacheKey(path));
        }

        if (descriptor is not null && !descriptor.Empty() && descriptor.Rows >= 2)
        {
            target.Add(descriptor);
        }
    }

    private static Mat? BuildLearnedTemplateDescriptor(string path, ORB orb)
    {
        try
        {
            using Mat sourceGray = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (sourceGray.Empty())
            {
                return null;
            }

            Mat workingGray = sourceGray;
            Mat? enlargedGray = null;
            if (sourceGray.Cols < 180)
            {
                double scale = Math.Clamp(260.0 / Math.Max(1, sourceGray.Cols), 1.0, 2.4);
                enlargedGray = new Mat();
                Cv2.Resize(
                    sourceGray,
                    enlargedGray,
                    new Size(),
                    scale,
                    scale,
                    InterpolationFlags.Cubic);
                workingGray = enlargedGray;
            }

            try
            {
                Rect roi = ToPixelRect(SlotPortraitRegions[^1], workingGray.Cols, workingGray.Rows);
                using var portrait = new Mat(workingGray, roi);
                var descriptors = new Mat();
                orb.DetectAndCompute(portrait, null, out _, descriptors);
                if (descriptors.Empty() || descriptors.Rows < 2)
                {
                    descriptors.Dispose();
                    return null;
                }

                return descriptors;
            }
            finally
            {
                enlargedGray?.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private static Mat? BuildTemplateDescriptor(string path, ORB orb)
    {
        try
        {
            using Mat grayscale = Cv2.ImRead(path, ImreadModes.Grayscale);
            if (grayscale.Empty())
            {
                return null;
            }

            var descriptors = new Mat();
            orb.DetectAndCompute(grayscale, null, out _, descriptors);
            if (descriptors.Empty() || descriptors.Rows < 2)
            {
                descriptors.Dispose();
                return null;
            }

            return descriptors;
        }
        catch
        {
            return null;
        }
    }

    private static List<Mat> BuildSlotDescriptors(byte[] encodedPng, ORB orb)
    {
        using Mat decodedGray = Cv2.ImDecode(encodedPng, ImreadModes.Grayscale);
        if (decodedGray.Empty())
        {
            return new List<Mat>();
        }

        using Mat sourceGray = decodedGray.Clone();
        Mat workingGray = sourceGray;
        Mat? enlargedGray = null;

        if (sourceGray.Cols < 180)
        {
            double scale = Math.Clamp(260.0 / Math.Max(1, sourceGray.Cols), 1.0, 2.4);
            enlargedGray = new Mat();
            Cv2.Resize(
                sourceGray,
                enlargedGray,
                new Size(),
                scale,
                scale,
                InterpolationFlags.Cubic);
            workingGray = enlargedGray;
        }

        try
        {
            var result = new List<Mat>(SlotPortraitRegions.Length);

            foreach (RelativeRect relativeRect in SlotPortraitRegions)
            {
                Rect roi = ToPixelRect(relativeRect, workingGray.Cols, workingGray.Rows);
                if (roi.Width < 24 || roi.Height < 24)
                {
                    continue;
                }

                using var portrait = new Mat(workingGray, roi);
                var descriptors = new Mat();
                orb.DetectAndCompute(portrait, null, out _, descriptors);
                if (!descriptors.Empty() && descriptors.Rows >= 2)
                {
                    result.Add(descriptors);
                }
                else
                {
                    descriptors.Dispose();
                }
            }

            return result;
        }
        finally
        {
            enlargedGray?.Dispose();
        }
    }

    private static IReadOnlyList<TemplateEntry> SelectTemplatesForFullScoring(
        IReadOnlyList<Mat> slotDescriptors,
        IReadOnlyList<TemplateEntry> templates,
        BFMatcher matcher,
        AttributeColorEvidence attributeEvidence)
    {
        if (templates.Count <= FullRefineCandidateCount || slotDescriptors.Count == 0)
        {
            return templates;
        }

        // 가장 넓은 얼굴 ROI 하나로 전체 DB를 먼저 빠르게 훑습니다.
        // 색상 같은 별도 휴리스틱으로 후보를 자르면 정답이 누락될 수 있어,
        // 1차도 동일한 ORB 매칭을 사용하고 상위 후보만 3개 ROI로 정밀 재검사합니다.
        Mat primaryQuery = slotDescriptors[^1];
        TemplatePreScore[] preScored = templates
            .Select(template => new TemplatePreScore(
                template,
                ScoreTemplateSingle(primaryQuery, template, matcher)))
            .OrderByDescending(item => item.Metrics.MatchCount)
            .ThenBy(item => item.Metrics.MeanDistance)
            .ThenBy(item => item.Template.Character.Name, StringComparer.Ordinal)
            .ToArray();

        TemplateEntry[] generalTop = preScored
            .Take(FullRefineCandidateCount)
            .Select(item => item.Template)
            .ToArray();

        if (attributeEvidence.Confidence < MinimumAttributeAssistConfidence ||
            string.IsNullOrWhiteSpace(attributeEvidence.Attribute))
        {
            return generalTop;
        }

        // 속성색이 꽤 확실할 때는 기존 ORB 상위 220개를 그대로 보존하면서,
        // 해당 속성 캐릭터 중 ORB 점수가 조금 낮아 잘릴 후보를 최대 80개 더 구제합니다.
        // 색 판정이 틀려도 기존 후보를 버리지 않으므로 정확도가 역으로 크게 떨어지지 않습니다.
        var selectedIds = generalTop
            .Select(item => item.Character.Id)
            .ToHashSet(StringComparer.Ordinal);
        var expanded = new List<TemplateEntry>(generalTop);

        foreach (TemplatePreScore item in preScored)
        {
            if (expanded.Count >= FullRefineCandidateCount + 80)
            {
                break;
            }

            if (selectedIds.Contains(item.Template.Character.Id) ||
                !CharacterSupportsAttribute(item.Template.Character, attributeEvidence.Attribute))
            {
                continue;
            }

            selectedIds.Add(item.Template.Character.Id);
            expanded.Add(item.Template);
        }

        return expanded;
    }

    private static MatchMetrics ScoreTemplateSingle(
        Mat query,
        TemplateEntry template,
        BFMatcher matcher)
    {
        int bestMatchCount = 0;
        double bestMeanDistance = double.MaxValue;
        foreach (Mat train in template.DescriptorSets)
        {
            MatchMetrics metrics = CountGoodMatches(query, train, matcher);
            if (metrics.MatchCount > bestMatchCount ||
                (metrics.MatchCount == bestMatchCount && metrics.MeanDistance < bestMeanDistance))
            {
                bestMatchCount = metrics.MatchCount;
                bestMeanDistance = metrics.MeanDistance;
            }
        }

        return new MatchMetrics(bestMatchCount, bestMeanDistance);
    }

    private static DeckScreenshotCandidate ScoreTemplate(
        IReadOnlyList<Mat> slotDescriptors,
        TemplateEntry template,
        BFMatcher matcher)
    {
        int bestMatchCount = 0;
        double bestMeanDistance = double.MaxValue;

        foreach (Mat query in slotDescriptors)
        {
            if (query.Empty() || query.Rows < 2)
            {
                continue;
            }

            foreach (Mat train in template.DescriptorSets)
            {
                MatchMetrics metrics = CountGoodMatches(query, train, matcher);
                if (metrics.MatchCount > bestMatchCount ||
                    (metrics.MatchCount == bestMatchCount && metrics.MeanDistance < bestMeanDistance))
                {
                    bestMatchCount = metrics.MatchCount;
                    bestMeanDistance = metrics.MeanDistance;
                }
            }
        }

        if (double.IsInfinity(bestMeanDistance) || double.IsNaN(bestMeanDistance))
        {
            bestMeanDistance = 999;
        }

        double similarity = Math.Clamp(0.45 + bestMatchCount / 40.0, 0.0, 0.98);
        return new DeckScreenshotCandidate(
            template.Character,
            similarity,
            bestMatchCount,
            bestMeanDistance);
    }

    private static int ScoreTemplateReciprocal(
        IReadOnlyList<Mat> slotDescriptors,
        TemplateEntry template,
        BFMatcher crossCheckMatcher)
    {
        int bestCount = 0;
        foreach (Mat query in slotDescriptors)
        {
            if (query.Empty() || query.Rows < 2)
            {
                continue;
            }

            foreach (Mat train in template.DescriptorSets)
            {
                if (train.Empty() || train.Rows < 2)
                {
                    continue;
                }

                DMatch[] matches;
                try
                {
                    matches = crossCheckMatcher.Match(query, train);
                }
                catch
                {
                    continue;
                }

                int count = matches.Count(match => match.Distance <= ReciprocalDistanceThreshold);
                if (count > bestCount)
                {
                    bestCount = count;
                }
            }
        }

        return bestCount;
    }

    private static MatchMetrics CountGoodMatches(Mat query, Mat train, BFMatcher matcher)
    {
        if (query.Empty() || train.Empty() || query.Rows < 2 || train.Rows < 2)
        {
            return new MatchMetrics(0, double.MaxValue);
        }

        DMatch[][] pairs;
        try
        {
            pairs = matcher.KnnMatch(query, train, 2);
        }
        catch
        {
            return new MatchMetrics(0, double.MaxValue);
        }

        int goodCount = 0;
        double distanceSum = 0;
        foreach (DMatch[] pair in pairs)
        {
            if (pair.Length < 2)
            {
                continue;
            }

            DMatch best = pair[0];
            DMatch second = pair[1];
            if (best.Distance < RatioTestThreshold * second.Distance)
            {
                goodCount++;
                distanceSum += best.Distance;
            }
        }

        return goodCount == 0
            ? new MatchMetrics(0, double.MaxValue)
            : new MatchMetrics(goodCount, distanceSum / goodCount);
    }

    private IReadOnlyDictionary<string, AttributeColorProfile> GetOrBuildLearnedAttributeProfiles()
    {
        IReadOnlyDictionary<string, AttributeColorProfile>? cached = _learnedAttributeProfiles;
        if (cached is not null)
        {
            return cached;
        }

        lock (_templateBuildLock)
        {
            cached = _learnedAttributeProfiles;
            if (cached is not null)
            {
                return cached;
            }

            IReadOnlyDictionary<string, IReadOnlyList<string>> references = _learningService.LoadReferenceMap();
            var characterById = _characters
                .GroupBy(character => character.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var signaturesByAttribute = new Dictionary<string, List<double[]>>(StringComparer.Ordinal);

            foreach ((string characterId, IReadOnlyList<string> paths) in references)
            {
                if (!characterById.TryGetValue(characterId, out CharacterEntry? character) || character is null)
                {
                    continue;
                }

                string attribute = DeckDataService.NormalizeAttribute(character.Attribute);
                if (attribute.Length == 0 || !IsPureAttributeLearningCharacter(character, attribute))
                {
                    continue;
                }

                if (!signaturesByAttribute.TryGetValue(attribute, out List<double[]>? signatures))
                {
                    signatures = new List<double[]>();
                    signaturesByAttribute[attribute] = signatures;
                }

                foreach (string path in paths)
                {
                    double[]? signature = BuildAttributeColorSignature(path);
                    if (signature is not null)
                    {
                        signatures.Add(signature);
                    }
                }
            }

            var result = new Dictionary<string, AttributeColorProfile>(StringComparer.Ordinal);
            foreach ((string attribute, List<double[]> signatures) in signaturesByAttribute)
            {
                if (signatures.Count == 0)
                {
                    continue;
                }

                double[] centroid = new double[AttributeHistogramBins];
                foreach (double[] signature in signatures)
                {
                    for (int index = 0; index < centroid.Length; index++)
                    {
                        centroid[index] += signature[index];
                    }
                }

                NormalizeVector(centroid);
                result[attribute] = new AttributeColorProfile(centroid, signatures.Count);
            }

            _learnedAttributeProfiles = result;
            return result;
        }
    }

    private static bool IsPureAttributeLearningCharacter(CharacterEntry character, string attribute)
    {
        if ((character.SubAttributes?.Count ?? 0) > 0)
        {
            return false;
        }

        foreach (CharacterForm form in character.AlternateForms ?? new List<CharacterForm>())
        {
            string formAttribute = DeckDataService.NormalizeAttribute(form.Attribute);
            if (formAttribute.Length > 0 && !string.Equals(formAttribute, attribute, StringComparison.Ordinal))
            {
                return false;
            }

            if ((form.SubAttributes?.Count ?? 0) > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static AttributeColorEvidence DetectAttributeColor(
        byte[] encodedPng,
        IReadOnlyDictionary<string, AttributeColorProfile> learnedProfiles,
        int slotIndex)
    {
        try
        {
            using Mat color = Cv2.ImDecode(encodedPng, ImreadModes.Color);
            if (color.Empty())
            {
                return AttributeColorEvidence.None;
            }

            if (learnedProfiles.Count >= 2)
            {
                double[]? signature = BuildAttributeColorSignature(color);
                if (signature is not null)
                {
                    var ranked = learnedProfiles
                        .Select(pair => new
                        {
                            Attribute = pair.Key,
                            Profile = pair.Value,
                            Score = DotProduct(signature, pair.Value.Centroid)
                        })
                        .OrderByDescending(item => item.Score)
                        .ToArray();

                    if (ranked.Length > 0)
                    {
                        double bestScore = ranked[0].Score;
                        double secondScore = ranked.Length > 1 ? ranked[1].Score : 0;
                        double margin = Math.Max(0, bestScore - secondScore);
                        double quality = Math.Clamp((bestScore - 0.58) / 0.32, 0, 1);
                        double separation = Math.Clamp(margin / 0.095, 0, 1);
                        double sampleFactor = Math.Clamp(ranked[0].Profile.SampleCount / 4.0, 0.45, 1.0);
                        double confidence = Math.Clamp(
                            (quality * 0.38 + separation * 0.62) * sampleFactor,
                            0,
                            1);

                        if (confidence >= 0.38)
                        {
                            return new AttributeColorEvidence(
                                ranked[0].Attribute,
                                confidence,
                                $"학습 {ranked[0].Profile.SampleCount}장");
                        }
                    }
                }
            }

            AttributeColorEvidence heuristic = DetectBasicAttributeFromBadge(color, slotIndex);
            return heuristic;
        }
        catch
        {
            return AttributeColorEvidence.None;
        }
    }

    private static double[]? BuildAttributeColorSignature(string path)
    {
        try
        {
            using Mat color = Cv2.ImRead(path, ImreadModes.Color);
            return color.Empty() ? null : BuildAttributeColorSignature(color);
        }
        catch
        {
            return null;
        }
    }

    private static double[]? BuildAttributeColorSignature(Mat color)
    {
        if (color.Empty() || color.Cols < 24 || color.Rows < 24)
        {
            return null;
        }

        using var hsv = new Mat();
        Cv2.CvtColor(color, hsv, ColorConversionCodes.BGR2HSV);

        int width = Math.Clamp((int)Math.Round(hsv.Cols * 0.72), 1, hsv.Cols);
        int height = Math.Clamp((int)Math.Round(hsv.Rows * 0.44), 1, hsv.Rows);
        var histogram = new double[AttributeHistogramBins];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Vec3b pixel = hsv.At<Vec3b>(y, x);
                int hue = pixel.Item0;
                int saturation = pixel.Item1;
                int value = pixel.Item2;
                if (saturation < 48 || value < 55)
                {
                    continue;
                }

                int bin = Math.Clamp(hue * AttributeHistogramBins / 180, 0, AttributeHistogramBins - 1);
                double saturationWeight = saturation / 255.0;
                double valueWeight = 0.35 + value / 255.0 * 0.65;
                double commonUiWeight = hue is >= 8 and <= 35
                    ? 0.38 // 공통 금색 카드 프레임
                    : hue is >= 86 and <= 105
                        ? 0.62 // 공통 청록 구슬/아이콘
                        : 1.0;
                histogram[bin] += saturationWeight * saturationWeight * valueWeight * commonUiWeight;
            }
        }

        double length = Math.Sqrt(histogram.Sum(value => value * value));
        if (length <= 0.0001)
        {
            return null;
        }

        NormalizeVector(histogram);
        return histogram;
    }

    private static AttributeColorEvidence DetectBasicAttributeFromBadge(Mat color, int slotIndex)
    {
        if (color.Empty() || color.Cols < 48 || color.Rows < 48)
        {
            return AttributeColorEvidence.None;
        }

        using var gray = new Mat();
        Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
        int roiWidth = Math.Clamp((int)Math.Round(gray.Cols * 0.72), 1, gray.Cols);
        int roiHeight = Math.Clamp((int)Math.Round(gray.Rows * 0.48), 1, gray.Rows);
        using var roi = new Mat(gray, new Rect(0, 0, roiWidth, roiHeight));
        using var blurred = new Mat();
        Cv2.GaussianBlur(roi, blurred, new Size(5, 5), 1.2);

        int minDimension = Math.Min(color.Cols, color.Rows);
        int minRadius = Math.Max(14, (int)Math.Round(minDimension * 0.085));
        int maxRadius = Math.Max(minRadius + 2, (int)Math.Round(minDimension * 0.20));
        CircleSegment[] circles;
        try
        {
            circles = Cv2.HoughCircles(
                blurred,
                HoughModes.Gradient,
                1.0,
                Math.Max(18, minDimension * 0.09),
                80,
                18,
                minRadius,
                maxRadius);
        }
        catch
        {
            return AttributeColorEvidence.None;
        }

        if (circles.Length == 0)
        {
            return AttributeColorEvidence.None;
        }

        using var hsv = new Mat();
        Cv2.CvtColor(color, hsv, ColorConversionCodes.BGR2HSV);

        string bestAttribute = string.Empty;
        double bestEvidenceScore = 0;
        double bestConfidence = 0;

        // v1.23.7의 휴대폰 기본 그리드 보정값에서는 12칸 모두 문자 원이
        // 슬롯 왼쪽 약 29%, 위쪽 약 14% 지점에 옵니다. 사용자가 영역을 약간
        // 다르게 잡더라도 아래 허용 오차 안에서 Hough 원을 찾습니다.
        double expectedCenterX = color.Cols * 0.29;
        double expectedCenterY = color.Rows * 0.14;
        double allowedXDistance = color.Cols * 0.18;
        double allowedYDistance = color.Rows * 0.16;

        foreach (CircleSegment circle in circles.Take(14))
        {
            double centerX = circle.Center.X;
            double centerY = circle.Center.Y;
            double radius = circle.Radius;
            double xDistance = Math.Abs(centerX - expectedCenterX);
            double yDistance = Math.Abs(centerY - expectedCenterY);
            if (centerX < 0 || centerY < 0 ||
                centerX > color.Cols * 0.72 || centerY > color.Rows * 0.48 ||
                xDistance > allowedXDistance || yDistance > allowedYDistance)
            {
                continue;
            }

            var votes = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["火"] = 0,
                ["水"] = 0,
                ["木"] = 0,
                ["光"] = 0,
                ["闇"] = 0
            };

            int x0 = Math.Max(0, (int)Math.Floor(centerX - radius * 0.82));
            int x1 = Math.Min(hsv.Cols - 1, (int)Math.Ceiling(centerX + radius * 0.82));
            int y0 = Math.Max(0, (int)Math.Floor(centerY - radius * 0.82));
            int y1 = Math.Min(hsv.Rows - 1, (int)Math.Ceiling(centerY + radius * 0.82));
            double innerRadiusSquared = radius * radius * 0.82 * 0.82;
            int coloredPixelCount = 0;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    double dx = x - centerX;
                    double dy = y - centerY;
                    if (dx * dx + dy * dy > innerRadiusSquared)
                    {
                        continue;
                    }

                    Vec3b pixel = hsv.At<Vec3b>(y, x);
                    int hue = pixel.Item0;
                    int saturation = pixel.Item1;
                    int value = pixel.Item2;
                    if (saturation < 78 || value < 60)
                    {
                        continue;
                    }

                    coloredPixelCount++;
                    if (hue <= 8 || hue >= 174)
                    {
                        votes["火"]++;
                    }
                    else if (hue is >= 98 and <= 119)
                    {
                        votes["水"]++;
                    }
                    else if (hue is >= 40 and <= 84)
                    {
                        votes["木"]++;
                    }
                    else if (hue is >= 20 and <= 38 && value >= 135)
                    {
                        votes["光"]++;
                    }
                    else if (hue is >= 123 and <= 160)
                    {
                        votes["闇"]++;
                    }
                }
            }

            if (coloredPixelCount < 80)
            {
                continue;
            }

            KeyValuePair<string, int>[] rankedVotes = votes
                .OrderByDescending(pair => pair.Value)
                .ToArray();
            string attribute = rankedVotes[0].Key;
            int topVotes = rankedVotes[0].Value;
            int secondVotes = rankedVotes.Length > 1 ? rankedVotes[1].Value : 0;
            if (topVotes < 36)
            {
                continue;
            }

            double purity = topVotes / (double)Math.Max(1, topVotes + secondVotes);
            double coverage = topVotes / (double)Math.Max(1, coloredPixelCount);
            double minimumPurity = string.Equals(attribute, "光", StringComparison.Ordinal) ? 0.72 : 0.58;
            if (purity < minimumPurity || coverage < 0.08)
            {
                continue;
            }

            double voteStrength = Math.Clamp(topVotes / 240.0, 0, 1);
            double confidence = Math.Clamp(
                (purity - minimumPurity) / Math.Max(0.01, 1.0 - minimumPurity) * 0.55 +
                voteStrength * 0.45,
                0,
                1);
            double positionDistance =
                xDistance / Math.Max(1.0, allowedXDistance) +
                yDistance / Math.Max(1.0, allowedYDistance);
            double positionWeight = Math.Clamp(1.15 - positionDistance * 0.42, 0.45, 1.15);
            double evidenceScore = topVotes * (0.5 + purity * 0.5) * positionWeight;

            if (confidence >= 0.42 && evidenceScore > bestEvidenceScore)
            {
                bestEvidenceScore = evidenceScore;
                bestAttribute = attribute;
                // 기본 HSV 규칙은 火/水/木/光/闇만 구분합니다. 天/冥/虹을
                // 光 등으로 오판할 여지가 있으므로 강한 자동확정 기준(72%)까지는
                // 올라가지 않도록 상한을 둡니다. 검수 학습 색상은 이 상한을 쓰지 않습니다.
                bestConfidence = Math.Min(confidence, 0.68);
            }
        }

        return bestAttribute.Length == 0 || bestConfidence < 0.55
            ? AttributeColorEvidence.None
            : new AttributeColorEvidence(bestAttribute, bestConfidence, "문자 테두리");
    }

    private static double GetAttributeRankingAdjustment(
        CharacterEntry character,
        AttributeColorEvidence evidence)
    {
        if (evidence.Confidence < MinimumAttributeAssistConfidence || evidence.Attribute.Length == 0)
        {
            return 0;
        }

        bool matches = CharacterSupportsAttribute(character, evidence.Attribute);
        bool learnedEvidence = evidence.Source.StartsWith("학습", StringComparison.Ordinal);

        if (matches)
        {
            // 검수로 누적된 실제 슬롯 색상은 강하게, 기본 문자 원 HSV 판정은
            // 보수적으로 가산합니다. ORB가 주 판정이라는 원칙은 유지합니다.
            return (learnedEvidence ? 2.8 : 0.8) * evidence.Confidence;
        }

        // 기본 HSV는 특수 속성(天/冥/虹)을 일부 기본 속성으로 착각할 수 있으므로
        // 불일치 후보를 감점하지 않습니다. 학습 색상만 아주 약하게 감점합니다.
        return learnedEvidence ? -0.7 * evidence.Confidence : 0;
    }

    public static bool CharacterSupportsAttribute(CharacterEntry character, string attribute)
    {
        string normalized = DeckDataService.NormalizeAttribute(attribute);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (string.Equals(DeckDataService.NormalizeAttribute(character.Attribute), normalized, StringComparison.Ordinal) ||
            DeckDataService.NormalizeAttributes(character.SubAttributes, character.Attribute)
                .Contains(normalized, StringComparer.Ordinal))
        {
            return true;
        }

        foreach (CharacterForm form in character.AlternateForms ?? new List<CharacterForm>())
        {
            string formAttribute = DeckDataService.NormalizeAttribute(form.Attribute);
            if (string.Equals(formAttribute, normalized, StringComparison.Ordinal) ||
                DeckDataService.NormalizeAttributes(form.SubAttributes, form.Attribute)
                    .Contains(normalized, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static double DotProduct(double[] left, double[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        double result = 0;
        for (int index = 0; index < length; index++)
        {
            result += left[index] * right[index];
        }
        return result;
    }

    private static void NormalizeVector(double[] values)
    {
        double length = Math.Sqrt(values.Sum(value => value * value));
        if (length <= 0.0001)
        {
            return;
        }

        for (int index = 0; index < values.Length; index++)
        {
            values[index] /= length;
        }
    }

    private static byte[] EncodeBitmapToPng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Rect ToPixelRect(RelativeRect rect, int width, int height)
    {
        int x0 = Math.Clamp((int)Math.Round(width * rect.Left), 0, Math.Max(0, width - 1));
        int y0 = Math.Clamp((int)Math.Round(height * rect.Top), 0, Math.Max(0, height - 1));
        int x1 = Math.Clamp((int)Math.Round(width * rect.Right), x0 + 1, width);
        int y1 = Math.Clamp((int)Math.Round(height * rect.Bottom), y0 + 1, height);
        return new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    private string GetDescriptorCacheKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{Path.GetFileName(path)}|{info.Length}";
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    private static string GetPersistentCachePath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string directory = Path.Combine(localAppData, "KotodamanWordFinder", "Cache");
        return Path.Combine(directory, $"deck_orb_v{PersistentCacheVersion}.bin");
    }

    private void LoadPersistentDescriptorCache()
    {
        if (!File.Exists(_persistentCachePath))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(
                _persistentCachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var reader = new BinaryReader(stream);

            if (reader.ReadInt32() != PersistentCacheMagic ||
                reader.ReadInt32() != PersistentCacheVersion)
            {
                return;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > 100_000)
            {
                return;
            }

            for (int index = 0; index < count; index++)
            {
                string key = reader.ReadString();
                int rows = reader.ReadInt32();
                int columns = reader.ReadInt32();
                int byteLength = reader.ReadInt32();

                if (rows < 2 || rows > 10_000 ||
                    columns <= 0 || columns > 256 ||
                    byteLength != checked(rows * columns) ||
                    byteLength > 10_000_000)
                {
                    return;
                }

                byte[] data = reader.ReadBytes(byteLength);
                if (data.Length != byteLength)
                {
                    return;
                }

                _persistentDescriptorCache[key] = new CachedDescriptorData(rows, columns, data);
            }
        }
        catch
        {
            _persistentDescriptorCache.Clear();
        }
    }

    private void PruneAndSavePersistentDescriptorCache()
    {
        string[] staleKeys = _persistentDescriptorCache.Keys
            .Where(key => !_usedPersistentCacheKeys.Contains(key))
            .ToArray();
        if (staleKeys.Length > 0)
        {
            foreach (string staleKey in staleKeys)
            {
                _persistentDescriptorCache.Remove(staleKey);
            }
            _persistentCacheDirty = true;
        }

        if (!_persistentCacheDirty)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_persistentCachePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = _persistentCachePath + ".tmp";
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None))
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(PersistentCacheMagic);
                    writer.Write(PersistentCacheVersion);
                    writer.Write(_persistentDescriptorCache.Count);
                    foreach ((string key, CachedDescriptorData cached) in _persistentDescriptorCache)
                    {
                        writer.Write(key);
                        writer.Write(cached.Rows);
                        writer.Write(cached.Columns);
                        writer.Write(cached.Data.Length);
                        writer.Write(cached.Data);
                    }
                }

                File.Move(temporaryPath, _persistentCachePath, overwrite: true);
                _persistentCacheDirty = false;
            }
            finally
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
                    // 캐시 정리 실패는 인식 기능에 영향을 주지 않습니다.
                }
            }
        }
        catch
        {
            // 캐시는 성능용이므로 저장 실패가 인식 자체를 막지 않게 합니다.
        }
    }

    private static CachedDescriptorData CreateCachedDescriptorData(Mat descriptor)
    {
        int byteLength = checked(descriptor.Rows * descriptor.Cols);
        byte[] data = new byte[byteLength];
        Marshal.Copy(descriptor.Data, data, 0, byteLength);
        return new CachedDescriptorData(descriptor.Rows, descriptor.Cols, data);
    }

    private static Mat CreateDescriptorMat(CachedDescriptorData cached)
    {
        var descriptor = new Mat(cached.Rows, cached.Columns, MatType.CV_8UC1);
        Marshal.Copy(cached.Data, 0, descriptor.Data, cached.Data.Length);
        return descriptor;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Mat? descriptor in _templateDescriptorCache.Values)
        {
            descriptor?.Dispose();
        }
        _templateDescriptorCache.Clear();
        _baseTemplates = null;
        _learnedTemplates = null;
        _learnedAttributeProfiles = null;
    }

    private sealed record TemplateEntry(CharacterEntry Character, IReadOnlyList<Mat> DescriptorSets);
    private sealed record RelativeRect(double Left, double Top, double Right, double Bottom);
    private sealed record MatchMetrics(int MatchCount, double MeanDistance);
    private sealed record TemplatePreScore(TemplateEntry Template, MatchMetrics Metrics);
    private sealed record RankedTemplateCandidate(
        TemplateEntry Template,
        DeckScreenshotCandidate Candidate,
        int ReciprocalMatchCount);
    private sealed record CachedDescriptorData(int Rows, int Columns, byte[] Data);
    private sealed record AttributeColorProfile(double[] Centroid, int SampleCount);
    private sealed record AttributeColorEvidence(string Attribute, double Confidence, string Source)
    {
        public static AttributeColorEvidence None { get; } = new(string.Empty, 0, string.Empty);
    }
}

public sealed record PreparedDeckScreenshotSlot(
    int SlotIndex,
    BitmapSource Crop,
    byte[] EncodedPng);

public sealed record DeckScreenshotCandidate(
    CharacterEntry Character,
    double Similarity,
    int MatchCount,
    double MeanDistance);

public sealed record DeckScreenshotSlotMatch(
    int SlotIndex,
    BitmapSource Crop,
    IReadOnlyList<DeckScreenshotCandidate> Candidates,
    string AttributeHint,
    double AttributeConfidence,
    string AttributeSource);
