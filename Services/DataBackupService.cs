using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace KotodamanWordFinder.Services;

/// <summary>
/// 사용자가 직접 만든 캐릭터 DB/이미지/덱/설정을 ZIP 한 파일로 백업하고 복원합니다.
/// 백업 폴더는 LocalApplicationData 아래의 전용 Backups 폴더를 사용합니다.
/// </summary>
public static class DataBackupService
{
    private const string BackupPrefix = "KotodamanUserData";
    private const string SafetyBackupPrefix = "BeforeRestore";
    private const int MaximumSafetyBackups = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string GetBackupDirectory(string dataDirectory)
    {
        _ = dataDirectory;
        Directory.CreateDirectory(AppPaths.BackupDirectory);
        return AppPaths.BackupDirectory;
    }

    public static IReadOnlyList<BackupArchiveInfo> ListBackups(string dataDirectory)
    {
        string backupDirectory = GetBackupDirectory(dataDirectory);
        if (!Directory.Exists(backupDirectory))
        {
            return Array.Empty<BackupArchiveInfo>();
        }

        return Directory
            .EnumerateFiles(backupDirectory, "*.zip", SearchOption.TopDirectoryOnly)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new BackupArchiveInfo(
                    path,
                    info.Name,
                    info.LastWriteTime,
                    info.Length,
                    info.Name.StartsWith(SafetyBackupPrefix, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();
    }

    public static string CreateManualBackup(string dataDirectory)
        => CreateBackup(dataDirectory, BackupPrefix);

    public static BackupRestoreResult RestoreBackup(
        string dataDirectory,
        string backupPath)
    {
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            throw new FileNotFoundException("복원할 백업 ZIP을 찾을 수 없습니다.", backupPath);
        }

        string safetyBackupPath = CreateBackup(dataDirectory, SafetyBackupPrefix);
        PruneSafetyBackups(dataDirectory);

        string fullDataDirectory = Path.GetFullPath(dataDirectory);
        string? projectDirectory = Path.GetDirectoryName(fullDataDirectory);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new InvalidOperationException("현재 Data 폴더의 상위 경로를 확인할 수 없습니다.");
        }

        string token = Guid.NewGuid().ToString("N");
        string restoreRoot = Path.Combine(projectDirectory, $".restore_temp_{token}");
        string oldDataDirectory = Path.Combine(projectDirectory, $".restore_old_{token}");
        string extractedDataDirectory = Path.Combine(restoreRoot, "Data");
        string extractedSettingsPath = Path.Combine(restoreRoot, "Settings", "settings.json");

        string settingsPath = GetSettingsPath();
        string settingsBackupPath = settingsPath + $".before_restore_{token}";
        bool movedCurrentData = false;
        bool installedRestoredData = false;
        bool backedUpSettings = false;

        try
        {
            Directory.CreateDirectory(restoreRoot);
            ExtractArchiveSafely(backupPath, restoreRoot);

            ValidateRestoredData(extractedDataDirectory);

            if (File.Exists(settingsPath))
            {
                string? settingsDirectory = Path.GetDirectoryName(settingsPath);
                if (!string.IsNullOrWhiteSpace(settingsDirectory))
                {
                    Directory.CreateDirectory(settingsDirectory);
                }

                File.Copy(settingsPath, settingsBackupPath, overwrite: true);
                backedUpSettings = true;
            }

            if (Directory.Exists(fullDataDirectory))
            {
                Directory.Move(fullDataDirectory, oldDataDirectory);
                movedCurrentData = true;
            }

            Directory.Move(extractedDataDirectory, fullDataDirectory);
            installedRestoredData = true;

            if (File.Exists(extractedSettingsPath))
            {
                string? settingsDirectory = Path.GetDirectoryName(settingsPath);
                if (!string.IsNullOrWhiteSpace(settingsDirectory))
                {
                    Directory.CreateDirectory(settingsDirectory);
                }

                File.Copy(extractedSettingsPath, settingsPath, overwrite: true);
            }

            DeleteRecognitionCacheSafely();

            if (Directory.Exists(oldDataDirectory))
            {
                Directory.Delete(oldDataDirectory, recursive: true);
            }

            if (File.Exists(settingsBackupPath))
            {
                File.Delete(settingsBackupPath);
            }

            return new BackupRestoreResult(safetyBackupPath);
        }
        catch
        {
            try
            {
                if (installedRestoredData && Directory.Exists(fullDataDirectory))
                {
                    Directory.Delete(fullDataDirectory, recursive: true);
                }

                if (movedCurrentData && Directory.Exists(oldDataDirectory))
                {
                    Directory.Move(oldDataDirectory, fullDataDirectory);
                }

                if (backedUpSettings && File.Exists(settingsBackupPath))
                {
                    string? settingsDirectory = Path.GetDirectoryName(settingsPath);
                    if (!string.IsNullOrWhiteSpace(settingsDirectory))
                    {
                        Directory.CreateDirectory(settingsDirectory);
                    }

                    File.Copy(settingsBackupPath, settingsPath, overwrite: true);
                }
            }
            catch
            {
                // 원래 예외를 유지합니다. 복원 전 안전 백업 ZIP은 이미 생성되어 있습니다.
            }

            throw;
        }
        finally
        {
            TryDeleteDirectory(restoreRoot);

            try
            {
                if (File.Exists(settingsBackupPath))
                {
                    File.Delete(settingsBackupPath);
                }
            }
            catch
            {
                // 임시 설정 백업 정리 실패는 복원 결과에 영향을 주지 않습니다.
            }
        }
    }

    public static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:N0} B";
        }

        double kib = bytes / 1024d;
        if (kib < 1024)
        {
            return $"{kib:N1} KB";
        }

        double mib = kib / 1024d;
        if (mib < 1024)
        {
            return $"{mib:N1} MB";
        }

        return $"{mib / 1024d:N2} GB";
    }

    private static string CreateBackup(string dataDirectory, string prefix)
    {
        string fullDataDirectory = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(fullDataDirectory))
        {
            throw new DirectoryNotFoundException($"Data 폴더를 찾을 수 없습니다.\n{fullDataDirectory}");
        }

        ValidateCurrentData(fullDataDirectory);

        string backupDirectory = GetBackupDirectory(fullDataDirectory);
        Directory.CreateDirectory(backupDirectory);

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = $"{prefix}_{timestamp}";
        string destinationPath = Path.Combine(backupDirectory, baseName + ".zip");
        int suffix = 2;
        while (File.Exists(destinationPath))
        {
            destinationPath = Path.Combine(backupDirectory, $"{baseName}_{suffix}.zip");
            suffix++;
        }

        string temporaryPath = destinationPath + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (string filePath in Directory.EnumerateFiles(
                             fullDataDirectory,
                             "*",
                             SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(fullDataDirectory, filePath);
                    if (ShouldSkipDataFile(relativePath))
                    {
                        continue;
                    }

                    string entryName = "Data/" + NormalizeArchivePath(relativePath);
                    archive.CreateEntryFromFile(
                        filePath,
                        entryName,
                        CompressionLevel.Fastest);
                }

                string settingsPath = GetSettingsPath();
                if (File.Exists(settingsPath))
                {
                    archive.CreateEntryFromFile(
                        settingsPath,
                        "Settings/settings.json",
                        CompressionLevel.Fastest);
                }

                WriteManifest(archive);
            }

            File.Move(temporaryPath, destinationPath);
            return destinationPath;
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
                // 원래 예외를 유지합니다.
            }

            throw;
        }
    }

    private static void ValidateCurrentData(string dataDirectory)
    {
        string charactersPath = Path.Combine(dataDirectory, "characters.json");
        string deckPath = Path.Combine(dataDirectory, "deck.json");

        if (!File.Exists(charactersPath))
        {
            throw new InvalidOperationException("characters.json이 없어 안전한 백업을 만들 수 없습니다.");
        }

        if (!File.Exists(deckPath))
        {
            throw new InvalidOperationException("deck.json이 없어 안전한 백업을 만들 수 없습니다.");
        }
    }

    private static void ValidateRestoredData(string extractedDataDirectory)
    {
        if (!Directory.Exists(extractedDataDirectory))
        {
            throw new InvalidDataException("이 ZIP에는 Data 폴더가 없어 백업 파일로 사용할 수 없습니다.");
        }

        string charactersPath = Path.Combine(extractedDataDirectory, "characters.json");
        string deckPath = Path.Combine(extractedDataDirectory, "deck.json");

        if (!File.Exists(charactersPath) || !File.Exists(deckPath))
        {
            throw new InvalidDataException(
                "백업 ZIP에 characters.json 또는 deck.json이 없습니다. 올바른 코토다망 데이터 백업인지 확인하세요.");
        }
    }

    private static bool ShouldSkipDataFile(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);

        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".optimize.tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".backup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void WriteManifest(ZipArchive archive)
    {
        var manifest = new
        {
            App = "KotodamanWordFinder",
            Format = 1,
            Version = AppPaths.AppVersion,
            CreatedAt = DateTimeOffset.Now,
            Note = "Data 폴더 전체와 사용자 설정을 백업한 파일입니다."
        };

        ZipArchiveEntry entry = archive.CreateEntry(
            "backup_manifest.json",
            CompressionLevel.Fastest);
        using Stream output = entry.Open();
        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(JsonSerializer.Serialize(manifest, JsonOptions));
    }

    private static void ExtractArchiveSafely(string backupPath, string destinationDirectory)
    {
        string fullDestination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(fullDestination);

        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            string targetPath = Path.GetFullPath(
                Path.Combine(
                    fullDestination,
                    entry.FullName.Replace('/', Path.DirectorySeparatorChar)));

            string destinationPrefix = fullDestination.EndsWith(Path.DirectorySeparatorChar)
                ? fullDestination
                : fullDestination + Path.DirectorySeparatorChar;

            if (!targetPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetPath, fullDestination, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("백업 ZIP에 허용되지 않는 경로가 포함되어 있습니다.");
            }

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            string? targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static void PruneSafetyBackups(string dataDirectory)
    {
        try
        {
            BackupArchiveInfo[] safetyBackups = ListBackups(dataDirectory)
                .Where(item => item.IsSafetyBackup)
                .OrderByDescending(item => item.CreatedAt)
                .ToArray();

            foreach (BackupArchiveInfo stale in safetyBackups.Skip(MaximumSafetyBackups))
            {
                try
                {
                    File.Delete(stale.Path);
                }
                catch
                {
                    // 오래된 안전 백업 정리 실패는 복원 기능을 막지 않습니다.
                }
            }
        }
        catch
        {
            // 정리 실패는 복원 기능을 막지 않습니다.
        }
    }

    private static string GetSettingsPath()
        => AppPaths.SettingsPath;

    private static void DeleteRecognitionCacheSafely()
    {
        try
        {
            string cacheDirectory = AppPaths.CacheDirectory;
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
        catch
        {
            // 인식 캐시는 성능용이므로 삭제 실패가 데이터 복원을 막지 않습니다.
        }
    }

    private static string NormalizeArchivePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

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
            // 임시 폴더 정리 실패는 원래 결과를 유지합니다.
        }
    }
}

public sealed record BackupArchiveInfo(
    string Path,
    string FileName,
    DateTime CreatedAt,
    long SizeBytes,
    bool IsSafetyBackup)
{
    public string KindText => IsSafetyBackup ? "복원 전 안전" : "수동";
    public string CreatedText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeText => DataBackupService.FormatByteSize(SizeBytes);
}

public sealed record BackupRestoreResult(string SafetyBackupPath);
