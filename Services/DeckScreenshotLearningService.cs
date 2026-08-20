using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KotodamanWordFinder.Services;

/// <summary>
/// 사용자가 덱 스크린샷 검수에서 확정한 실제 슬롯 이미지를 캐릭터별로 보관합니다.
/// UI가 바뀔 때 과거 학습 이미지가 오히려 오인식을 만들지 않도록 프로필 단위로 격리합니다.
/// </summary>
public sealed class DeckScreenshotLearningService
{
    public const string CurrentUiProfile = "deck-ui-2026-v1";
    public const string CurrentUiProfileDisplayName = "현행 덱 UI v1";

    private const int MaxSamplesPerCharacter = 3;
    private const int MaximumStoredWidth = 240;

    private readonly string _profileDirectory;

    public DeckScreenshotLearningService(string dataDirectory)
    {
        string root = Path.Combine(Path.GetFullPath(dataDirectory), "RecognitionReferences");
        _profileDirectory = Path.Combine(root, CurrentUiProfile);
    }

    public LearningSampleStats GetStats()
    {
        if (!Directory.Exists(_profileDirectory))
        {
            return new LearningSampleStats(0, 0, 0);
        }

        int characterCount = 0;
        int sampleCount = 0;
        long totalBytes = 0;

        try
        {
            foreach (string characterDirectory in Directory.EnumerateDirectories(_profileDirectory))
            {
                string[] files = Directory.EnumerateFiles(characterDirectory, "*.png")
                    .ToArray();
                if (files.Length == 0)
                {
                    continue;
                }

                characterCount++;
                sampleCount += files.Length;
                foreach (string file in files)
                {
                    try
                    {
                        totalBytes += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // 통계 실패는 학습 기능을 막지 않습니다.
                    }
                }
            }
        }
        catch
        {
            return new LearningSampleStats(characterCount, sampleCount, totalBytes);
        }

        return new LearningSampleStats(characterCount, sampleCount, totalBytes);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> LoadReferenceMap()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (!Directory.Exists(_profileDirectory))
        {
            return result;
        }

        try
        {
            foreach (string characterDirectory in Directory.EnumerateDirectories(_profileDirectory))
            {
                string characterId = Path.GetFileName(characterDirectory);
                if (string.IsNullOrWhiteSpace(characterId))
                {
                    continue;
                }

                string[] files = Directory.EnumerateFiles(characterDirectory, "*.png")
                    .OrderByDescending(path =>
                    {
                        try
                        {
                            return File.GetLastWriteTimeUtc(path);
                        }
                        catch
                        {
                            return DateTime.MinValue;
                        }
                    })
                    .Take(MaxSamplesPerCharacter)
                    .ToArray();

                if (files.Length > 0)
                {
                    result[characterId] = files;
                }
            }
        }
        catch
        {
            // 손상된 학습 폴더 하나 때문에 기본 이미지 인식까지 실패하지 않게 합니다.
        }

        return result;
    }

    public LearningSaveResult SaveVerifiedSamples(IEnumerable<VerifiedDeckSlotSample> samples)
    {
        int added = 0;
        int duplicates = 0;
        int failed = 0;

        Directory.CreateDirectory(_profileDirectory);

        foreach (VerifiedDeckSlotSample sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.CharacterId) || sample.Crop is null)
            {
                failed++;
                continue;
            }

            try
            {
                byte[] pngBytes = EncodeNormalizedPng(sample.Crop);
                string hash = Convert.ToHexString(SHA256.HashData(pngBytes))
                    .ToLowerInvariant()[..16];
                string characterDirectory = Path.Combine(
                    _profileDirectory,
                    MakeSafeDirectoryName(sample.CharacterId));
                Directory.CreateDirectory(characterDirectory);

                string destinationPath = Path.Combine(characterDirectory, $"slot-{hash}.png");
                if (File.Exists(destinationPath))
                {
                    duplicates++;
                    TouchFile(destinationPath);
                    TrimOldSamples(characterDirectory);
                    continue;
                }

                string temporaryPath = destinationPath + ".tmp";
                try
                {
                    File.WriteAllBytes(temporaryPath, pngBytes);
                    File.Move(temporaryPath, destinationPath, overwrite: false);
                    added++;
                }
                finally
                {
                    TryDelete(temporaryPath);
                }

                TrimOldSamples(characterDirectory);
            }
            catch
            {
                failed++;
            }
        }

        return new LearningSaveResult(added, duplicates, failed);
    }

    public bool ClearCurrentProfile()
    {
        try
        {
            if (Directory.Exists(_profileDirectory))
            {
                Directory.Delete(_profileDirectory, recursive: true);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] EncodeNormalizedPng(BitmapSource source)
    {
        BitmapSource normalized = source;
        if (source.PixelWidth > MaximumStoredWidth)
        {
            double scale = MaximumStoredWidth / (double)Math.Max(1, source.PixelWidth);
            var transformed = new TransformedBitmap(
                source,
                new ScaleTransform(scale, scale));
            if (transformed.CanFreeze)
            {
                transformed.Freeze();
            }
            normalized = transformed;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(normalized));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string MakeSafeDirectoryName(string characterId)
    {
        var builder = new StringBuilder(characterId.Length);
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        foreach (char character in characterId)
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        string result = builder.ToString().Trim();
        if (result.Length > 120)
        {
            string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(characterId)))
                .ToLowerInvariant()[..12];
            result = result[..100] + "-" + suffix;
        }

        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private static void TrimOldSamples(string characterDirectory)
    {
        try
        {
            string[] files = Directory.EnumerateFiles(characterDirectory, "*.png")
                .OrderByDescending(path =>
                {
                    try
                    {
                        return File.GetLastWriteTimeUtc(path);
                    }
                    catch
                    {
                        return DateTime.MinValue;
                    }
                })
                .ToArray();

            foreach (string stale in files.Skip(MaxSamplesPerCharacter))
            {
                TryDelete(stale);
            }
        }
        catch
        {
            // 정리 실패는 다음 학습 저장에서 다시 시도할 수 있습니다.
        }
    }

    private static void TouchFile(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // 중복 샘플의 최근 사용 시각 갱신 실패는 무시합니다.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 정리 실패는 학습 자체를 막지 않습니다.
        }
    }
}

public sealed record VerifiedDeckSlotSample(string CharacterId, BitmapSource Crop);

public sealed record LearningSampleStats(int CharacterCount, int SampleCount, long TotalBytes)
{
    public string SizeText => TotalBytes < 1024 * 1024
        ? $"{TotalBytes / 1024.0:0.0} KB"
        : $"{TotalBytes / 1024.0 / 1024.0:0.0} MB";
}

public sealed record LearningSaveResult(int AddedCount, int DuplicateCount, int FailedCount);
