using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StbImageSharp;

namespace KotodamanWordFinder.Services;

public static class CharacterImageService
{
    private const int MaximumStoredImageWidth = 512;
    private const int StoredColorBitsPerChannel = 5;
    private const string ColorOptimizationMarkerFileName = ".character-images-color-v2";

    private static readonly ConcurrentDictionary<string, WeakReference<BitmapSource>> ThumbnailCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SupportedExtensions = new(
        new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga", ".psd", ".tif", ".tiff" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 이미지 저장 위치를 Data/CharacterImages로 고정합니다.
    /// 원본 이미지는 순수 C# 디코더로 읽고 표준 PNG로 저장합니다.
    /// </summary>
    public static string GetImageDirectory(string dataDirectory)
        => Path.Combine(Path.GetFullPath(dataDirectory), "CharacterImages");

    public static bool IsSupportedImageFile(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && SupportedExtensions.Contains(Path.GetExtension(path));

    public static string GetSupportedFormatText()
        => "PNG, JPG, JPEG, BMP, GIF, TGA, PSD, TIFF";

    public static string GetDialogFilter()
        => "지원 이미지 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tga;*.psd;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tga;*.psd;*.tif;*.tiff";

    public static string? ResolveImagePath(string dataDirectory, string? imageFileName)
    {
        if (string.IsNullOrWhiteSpace(imageFileName))
        {
            return null;
        }

        if (Path.IsPathRooted(imageFileName) && File.Exists(imageFileName))
        {
            return Path.GetFullPath(imageFileName);
        }

        string fileName = Path.GetFileName(imageFileName);
        if (fileName.Length == 0)
        {
            return null;
        }

        foreach (string directory in GetCandidateImageDirectories(dataDirectory))
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string SaveImageCopy(
        string sourcePath,
        string dataDirectory,
        string characterId,
        string? previousImageFileName = null)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("선택한 이미지 파일을 찾을 수 없습니다.", sourcePath);
        }

        if (!IsSupportedImageFile(sourcePath))
        {
            throw new InvalidOperationException(
                $"지원하지 않는 이미지 형식입니다. {GetSupportedFormatText()} 파일을 선택하세요.");
        }

        string imageDirectory = GetImageDirectory(dataDirectory);
        Directory.CreateDirectory(imageDirectory);

        string safeCharacterId = MakeSafeFileName(characterId);
        string fileName = $"{safeCharacterId}.png";
        string destinationPath = Path.Combine(imageDirectory, fileName);
        string temporaryPath = destinationPath + ".tmp";

        try
        {
            BitmapSource bitmap = DecodeImage(sourcePath, 0)
                ?? throw new InvalidDataException("이미지 디코더가 파일 내용을 인식하지 못했습니다.");

            // 캐릭터 목록 썸네일과 덱 스크린샷 특징점 비교에는 1024px 원본이 필요하지 않습니다.
            // 웹에서 간혹 1024px 이미지를 받아 그대로 PNG로 보관하면 캐릭터 몇 명만으로도
            // 수십 MB가 늘어나므로 저장 시 최대 폭을 제한합니다.
            bitmap = ScaleBitmap(bitmap, MaximumStoredImageWidth);
            bitmap = OptimizeBitmapForPngStorage(bitmap);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                encoder.Save(output);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        catch (Exception exception)
        {
            TryDeleteFile(temporaryPath);
            throw new InvalidOperationException(
                $"선택한 이미지를 읽거나 PNG로 변환할 수 없습니다. ({exception.GetType().Name}: {exception.Message})",
                exception);
        }

        if (!string.IsNullOrWhiteSpace(previousImageFileName) &&
            !string.Equals(
                Path.GetFileName(previousImageFileName),
                fileName,
                StringComparison.OrdinalIgnoreCase))
        {
            DeleteImage(dataDirectory, previousImageFileName);
        }

        return fileName;
    }

    public static ImageLibraryOptimizationResult OptimizeImageLibrary(
        string dataDirectory,
        IEnumerable<string?> referencedImageFileNames)
    {
        string imageDirectory = GetImageDirectory(dataDirectory);
        if (!Directory.Exists(imageDirectory))
        {
            return new ImageLibraryOptimizationResult();
        }

        var referenced = referencedImageFileNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => Path.GetFileName(name!))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        long bytesBefore = Directory.EnumerateFiles(imageDirectory)
            .Select(path => new FileInfo(path).Length)
            .Sum();
        int deletedOrphanCount = 0;
        long deletedOrphanBytes = 0;
        int resizedCount = 0;
        int colorOptimizedCount = 0;

        string colorMarkerPath = Path.Combine(
            Path.GetFullPath(dataDirectory),
            ColorOptimizationMarkerFileName);
        bool needsColorMigration = !File.Exists(colorMarkerPath);

        foreach (string path in Directory.EnumerateFiles(imageDirectory).ToArray())
        {
            string fileName = Path.GetFileName(path);
            if (!referenced.Contains(fileName))
            {
                try
                {
                    long length = new FileInfo(path).Length;
                    File.Delete(path);
                    deletedOrphanCount++;
                    deletedOrphanBytes += length;
                }
                catch
                {
                    // 정리 실패는 캐릭터 데이터 저장을 막지 않습니다.
                }
                continue;
            }

            if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                BitmapSource? bitmap = DecodeImage(path, 0);
                if (bitmap is null)
                {
                    continue;
                }

                bool resized = bitmap.PixelWidth > MaximumStoredImageWidth;
                if (resized)
                {
                    bitmap = ScaleBitmap(bitmap, MaximumStoredImageWidth);
                }

                if (!resized && !needsColorMigration)
                {
                    continue;
                }

                BitmapSource optimized = OptimizeBitmapForPngStorage(bitmap);
                string temporaryPath = path + ".optimize.tmp";
                try
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(optimized));
                    using (var output = new FileStream(
                               temporaryPath,
                               FileMode.Create,
                               FileAccess.Write,
                               FileShare.None))
                    {
                        encoder.Save(output);
                    }

                    long originalLength = new FileInfo(path).Length;
                    long optimizedLength = new FileInfo(temporaryPath).Length;
                    if (resized || optimizedLength < originalLength)
                    {
                        File.Move(temporaryPath, path, overwrite: true);
                        if (resized)
                        {
                            resizedCount++;
                        }
                        if (needsColorMigration)
                        {
                            colorOptimizedCount++;
                        }
                    }
                }
                finally
                {
                    TryDeleteFile(temporaryPath);
                }
            }
            catch
            {
                // 한 이미지 최적화 실패 때문에 전체 저장이 실패하지 않게 합니다.
            }
        }

        if (needsColorMigration)
        {
            try
            {
                File.WriteAllText(
                    colorMarkerPath,
                    $"v2 {DateTime.UtcNow:O} bits={StoredColorBitsPerChannel}");
            }
            catch
            {
                // 마커 저장 실패 시 다음 저장에서 다시 확인해도 기능상 문제는 없습니다.
            }
        }

        long bytesAfter = Directory.Exists(imageDirectory)
            ? Directory.EnumerateFiles(imageDirectory)
                .Select(path => new FileInfo(path).Length)
                .Sum()
            : 0;

        return new ImageLibraryOptimizationResult
        {
            DeletedOrphanCount = deletedOrphanCount,
            DeletedOrphanBytes = deletedOrphanBytes,
            ResizedCount = resizedCount,
            ColorOptimizedCount = colorOptimizedCount,
            BytesBefore = bytesBefore,
            BytesAfter = bytesAfter
        };
    }

    public sealed class ImageLibraryOptimizationResult
    {
        public int DeletedOrphanCount { get; init; }
        public long DeletedOrphanBytes { get; init; }
        public int ResizedCount { get; init; }
        public int ColorOptimizedCount { get; init; }
        public long BytesBefore { get; init; }
        public long BytesAfter { get; init; }
        public long SavedBytes => Math.Max(0, BytesBefore - BytesAfter);
    }

    public static void DeleteImage(string dataDirectory, string? imageFileName)
    {
        string? path = ResolveImagePath(dataDirectory, imageFileName);
        if (path is null)
        {
            return;
        }

        TryDeleteFile(path);
    }

    public static BitmapSource? LoadBitmap(
        string dataDirectory,
        string? imageFileName,
        int decodePixelWidth = 0)
    {
        string? path = ResolveImagePath(dataDirectory, imageFileName);
        return path is null ? null : LoadBitmapFromPath(path, decodePixelWidth);
    }

    public static BitmapSource? LoadBitmapFromPath(string? path, int decodePixelWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        if (decodePixelWidth <= 0)
        {
            return DecodeImage(path, decodePixelWidth);
        }

        string cacheKey;
        try
        {
            var info = new FileInfo(path);
            cacheKey = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{decodePixelWidth}";
        }
        catch
        {
            cacheKey = $"{Path.GetFullPath(path)}|{decodePixelWidth}";
        }

        if (ThumbnailCache.TryGetValue(cacheKey, out WeakReference<BitmapSource>? cachedReference) &&
            cachedReference.TryGetTarget(out BitmapSource? cachedBitmap))
        {
            return cachedBitmap;
        }

        BitmapSource? bitmap = DecodeImage(path, decodePixelWidth);
        if (bitmap is not null)
        {
            ThumbnailCache[cacheKey] = new WeakReference<BitmapSource>(bitmap);
            TrimDeadThumbnailCacheEntries();
        }
        return bitmap;
    }

    public static void ClearThumbnailCache()
        => ThumbnailCache.Clear();

    /// <summary>
    /// GameWith 캐릭터 일러스트 대신 속성/종족 아이콘 같은 작은 이미지를 잘못 고르는 일을 막습니다.
    /// 현재 GameWith 캐릭터 이미지는 보통 250x208 전후이므로 너무 작은 이미지는 자동 등록에서 제외합니다.
    /// </summary>
    public static bool IsLikelyCharacterArtwork(string? path, out int width, out int height)
    {
        width = 0;
        height = 0;
        BitmapSource? bitmap = LoadBitmapFromPath(path, 0);
        if (bitmap is null)
        {
            return false;
        }

        width = bitmap.PixelWidth;
        height = bitmap.PixelHeight;

        // 130x117 / 130x130 계열의 속성·종족 아이콘은 확실히 배제합니다.
        // 너무 빡빡하게 잡지 않아 사이트 이미지 크기가 조금 바뀌어도 캐릭터 이미지는 통과하게 합니다.
        return width >= 170 && height >= 145;
    }

    private static BitmapSource? DecodeImage(string path, int decodePixelWidth)
    {
        // 목록용 작은 썸네일은 Windows WIC가 원본 전체를 RGBA 배열로 펼치기 전에
        // DecodePixelWidth 단계에서 바로 축소하도록 맡깁니다.
        // 2,500명 이상 DB에서도 화면에 보이는 항목만 빠르게 로드할 수 있습니다.
        if (decodePixelWidth > 0 && TryDecodeThumbnailWithWic(path, decodePixelWidth, out BitmapSource? thumbnail))
        {
            return thumbnail;
        }

        // 원본 크기가 필요한 저장/검사 작업과 WIC 미지원 포맷은 기존 순수 C# 디코더를 사용합니다.
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            ImageResult image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);

            if (image.Width <= 0 || image.Height <= 0)
            {
                return null;
            }

            int expectedLength = checked(image.Width * image.Height * 4);
            if (image.Data.Length < expectedLength)
            {
                return null;
            }

            // StbImageSharp는 RGBA, WPF의 Bgra32는 BGRA 순서이므로 R/B를 교환합니다.
            byte[] bgraPixels = new byte[expectedLength];
            for (int index = 0; index < expectedLength; index += 4)
            {
                bgraPixels[index] = image.Data[index + 2];
                bgraPixels[index + 1] = image.Data[index + 1];
                bgraPixels[index + 2] = image.Data[index];
                bgraPixels[index + 3] = image.Data[index + 3];
            }

            int stride = checked(image.Width * 4);
            BitmapSource bitmap = BitmapSource.Create(
                image.Width,
                image.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                bgraPixels,
                stride);
            bitmap.Freeze();

            return ScaleBitmap(bitmap, decodePixelWidth);
        }
        catch
        {
            // TIFF 등 Windows 기본 디코더가 더 잘 처리하는 형식은 아래에서 다시 시도합니다.
        }

        return TryDecodeWithWpf(path, decodePixelWidth);
    }

    private static BitmapSource? TryDecodeWithWpf(string path, int decodePixelWidth)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes, writable: false);
            BitmapDecoder decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            BitmapSource frame = decoder.Frames[0];
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }

            return ScaleBitmap(frame, decodePixelWidth);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryDecodeThumbnailWithWic(
        string path,
        int decodePixelWidth,
        out BitmapSource? bitmap)
    {
        bitmap = null;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = Math.Max(1, decodePixelWidth);
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            bitmap = image;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource ScaleBitmap(BitmapSource source, int decodePixelWidth)
    {
        if (decodePixelWidth <= 0 || source.PixelWidth <= decodePixelWidth)
        {
            return source;
        }

        double scale = decodePixelWidth / (double)source.PixelWidth;
        var transformed = new TransformedBitmap(
            source,
            new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static BitmapSource OptimizeBitmapForPngStorage(BitmapSource source)
    {
        BitmapSource quantized = ReduceColorDepthForStorage(source);
        return TryCreateLosslessIndexed8(quantized) ?? quantized;
    }

    private static BitmapSource? TryCreateLosslessIndexed8(BitmapSource source)
    {
        BitmapSource bgraSource = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = source;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            bgraSource = converted;
        }

        int width = bgraSource.PixelWidth;
        int height = bgraSource.PixelHeight;
        int sourceStride = checked(width * 4);
        byte[] sourcePixels = new byte[checked(sourceStride * height)];
        bgraSource.CopyPixels(sourcePixels, sourceStride, 0);

        var colorToIndex = new Dictionary<uint, byte>();
        var colors = new List<Color>(256);
        int indexedStride = width;
        byte[] indexedPixels = new byte[checked(indexedStride * height)];

        for (int pixel = 0, output = 0; pixel < sourcePixels.Length; pixel += 4, output++)
        {
            uint key = (uint)(sourcePixels[pixel] |
                              sourcePixels[pixel + 1] << 8 |
                              sourcePixels[pixel + 2] << 16 |
                              sourcePixels[pixel + 3] << 24);
            if (!colorToIndex.TryGetValue(key, out byte index))
            {
                if (colors.Count >= 256)
                {
                    return null;
                }

                index = (byte)colors.Count;
                colorToIndex[key] = index;
                colors.Add(Color.FromArgb(
                    sourcePixels[pixel + 3],
                    sourcePixels[pixel + 2],
                    sourcePixels[pixel + 1],
                    sourcePixels[pixel]));
            }

            indexedPixels[output] = index;
        }

        var palette = new BitmapPalette(colors);
        BitmapSource indexed = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Indexed8,
            palette,
            indexedPixels,
            indexedStride);
        indexed.Freeze();
        return indexed;
    }

    private static BitmapSource ReduceColorDepthForStorage(BitmapSource source)
    {
        BitmapSource bgraSource = source;
        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = source;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            bgraSource = converted;
        }

        int width = bgraSource.PixelWidth;
        int height = bgraSource.PixelHeight;
        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        bgraSource.CopyPixels(pixels, stride, 0);

        int shift = 8 - StoredColorBitsPerChannel;
        int mask = 0xFF << shift & 0xFF;
        int midpoint = 1 << Math.Max(0, shift - 1);
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = QuantizeChannel(pixels[index], mask, midpoint);
            pixels[index + 1] = QuantizeChannel(pixels[index + 1], mask, midpoint);
            pixels[index + 2] = QuantizeChannel(pixels[index + 2], mask, midpoint);
            // alpha는 그대로 보존합니다.
        }

        BitmapSource result = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        result.Freeze();
        return result;
    }

    private static byte QuantizeChannel(byte value, int mask, int midpoint)
    {
        if (value is 0 or 255)
        {
            return value;
        }

        int quantized = (value & mask) + midpoint;
        return (byte)Math.Min(255, quantized);
    }

    private static void TrimDeadThumbnailCacheEntries()
    {
        if (ThumbnailCache.Count < 384)
        {
            return;
        }

        foreach ((string key, WeakReference<BitmapSource> reference) in ThumbnailCache.ToArray())
        {
            if (!reference.TryGetTarget(out _))
            {
                ThumbnailCache.TryRemove(key, out _);
            }
        }
    }

    private static string MakeSafeFileName(string value)
    {
        string safeValue = new(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());

        return string.IsNullOrWhiteSpace(safeValue)
            ? Guid.NewGuid().ToString("N")
            : safeValue;
    }

    private static void TryDeleteFile(string path)
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
            // 이미지 삭제 실패가 캐릭터 데이터 저장을 막지는 않게 합니다.
        }
    }

    private static IEnumerable<string> GetCandidateImageDirectories(string dataDirectory)
    {
        string normalizedDataDirectory = Path.GetFullPath(dataDirectory);
        string? projectDirectory = Directory.GetParent(normalizedDataDirectory)?.FullName;

        string? bundledImageDirectory = string.IsNullOrWhiteSpace(AppPaths.BundledDataDirectory)
            ? null
            : Path.Combine(AppPaths.BundledDataDirectory, "CharacterImages");

        var directories = new[]
        {
            // 사용자가 교체하거나 새로 받은 이미지를 가장 먼저 사용합니다.
            GetImageDirectory(normalizedDataDirectory),
            // 배포본의 기본 이미지는 읽기 전용으로 그대로 재사용해 140MB 이상을 복사하지 않습니다.
            bundledImageDirectory,
            projectDirectory is null
                ? null
                : Path.Combine(projectDirectory, "Images", "Characters"),
            Path.Combine(AppContext.BaseDirectory, "Images", "Characters"),
            Path.Combine(Directory.GetCurrentDirectory(), "Images", "Characters"),
            Path.Combine(normalizedDataDirectory, "Images", "Characters")
        };

        return directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
