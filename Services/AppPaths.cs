using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KotodamanWordFinder.Models;

namespace KotodamanWordFinder.Services;

/// <summary>
/// 프로그램에 포함된 읽기 전용 기본 데이터와 사용자가 수정하는 데이터를 분리합니다.
/// 기본 이미지/사전은 배포 폴더에서 읽고, 캐릭터 편집·덱·프리셋·학습 자료는
/// LocalApplicationData 아래에 저장합니다.
/// </summary>
public static class AppPaths
{
    public const string ProductName = "KotodamanWordFinder";
    public const string DisplayName = "코토다망 최장 단어 탐색기";

    // 버전은 csproj의 <Version> 하나만 고칩니다.
    // 코드에 문자열로 또 적어두면 창 제목·데이터 업데이트 최소버전 검사와 따로 놀게 됩니다.
    public static readonly string AppVersion = ReadAppVersion();

    public const string InitialDataVersion = "2026.08.19.1";
    public const string DefaultDataVersion = InitialDataVersion;

    private static readonly object SyncRoot = new();
    private static bool _isInitialized;

    public static string UserRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    public static string UserDataDirectory => Path.Combine(UserRootDirectory, "Data");
    public static string UserImageDirectory => Path.Combine(UserDataDirectory, "CharacterImages");
    public static string LogDirectory => Path.Combine(UserRootDirectory, "Logs");
    public static string BackupDirectory => Path.Combine(UserRootDirectory, "Backups");
    public static string CacheDirectory => Path.Combine(UserRootDirectory, "Cache");
    public static string SettingsPath => Path.Combine(UserRootDirectory, "settings.json");
    public static string DataStatePath => Path.Combine(UserRootDirectory, "data_state.json");

    public static string BundledDataDirectory { get; private set; } = string.Empty;
    public static string BundledDataVersion { get; private set; } = DefaultDataVersion;
    public static string UserDataVersion { get; private set; } = DefaultDataVersion;
    public static DataInitializationResult LastInitializationResult { get; private set; } = new();

    public static DataInitializationResult Initialize()
    {
        lock (SyncRoot)
        {
            if (_isInitialized)
            {
                return LastInitializationResult;
            }

            BundledDataDirectory = FindBundledDataDirectory()
                ?? throw new DirectoryNotFoundException(
                    $"프로그램 기본 Data 폴더를 찾을 수 없습니다.\n" +
                    $"실행 폴더: {AppContext.BaseDirectory}\n" +
                    $"현재 폴더: {Directory.GetCurrentDirectory()}");

            Directory.CreateDirectory(UserRootDirectory);
            Directory.CreateDirectory(UserDataDirectory);
            Directory.CreateDirectory(UserImageDirectory);
            Directory.CreateDirectory(LogDirectory);
            Directory.CreateDirectory(BackupDirectory);
            Directory.CreateDirectory(CacheDirectory);

            LastInitializationResult = SynchronizeBundledDataCore(forceRestoreMissingBundledCharacters: false);
            _isInitialized = true;
            return LastInitializationResult;
        }
    }

    /// <summary>
    /// 백업 복원 뒤처럼 실행 중 사용자 Data가 교체된 경우 기본 데이터와 다시 동기화합니다.
    /// 기존 사용자 캐릭터는 보존하고, 새로 배포된 캐릭터/학습 파일만 추가합니다.
    /// </summary>
    public static DataInitializationResult SynchronizeBundledData(
        bool forceRestoreMissingBundledCharacters = false)
    {
        lock (SyncRoot)
        {
            if (!_isInitialized)
            {
                return Initialize();
            }

            LastInitializationResult = SynchronizeBundledDataCore(
                forceRestoreMissingBundledCharacters);
            return LastInitializationResult;
        }
    }

    public static string RefreshUserDataVersion()
    {
        lock (SyncRoot)
        {
            EnsureInitialized();
            UserDataVersion = ReadDataVersion(
                Path.Combine(UserDataDirectory, "data_manifest.json"),
                BundledDataVersion);
            return UserDataVersion;
        }
    }

    public static string GetBundledDataPath(string fileName)
    {
        EnsureInitialized();
        return Path.Combine(BundledDataDirectory, fileName);
    }

    public static string GetUserDataPath(string fileName)
    {
        EnsureInitialized();
        return Path.Combine(UserDataDirectory, fileName);
    }

    public static string ResolveUserOrBundledDataPath(string fileName)
    {
        EnsureInitialized();

        string userPath = Path.Combine(UserDataDirectory, fileName);
        if (File.Exists(userPath) || File.Exists(userPath + ".gz"))
        {
            return userPath;
        }

        return Path.Combine(BundledDataDirectory, fileName);
    }

    private static string ReadAppVersion()
    {
        Assembly assembly = typeof(AppPaths).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // SDK가 "1.25.1+커밋해시" 형태로 붙이는 소스 리비전은 잘라냅니다.
            int suffixIndex = informational.IndexOf('+');
            return (suffixIndex < 0 ? informational : informational[..suffixIndex]).Trim();
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            Initialize();
        }
    }

    private static DataInitializationResult SynchronizeBundledDataCore(
        bool forceRestoreMissingBundledCharacters)
    {
        bool createdUserData = !File.Exists(Path.Combine(UserDataDirectory, "characters.json"));
        int copiedFileCount = 0;
        int copiedReferenceFileCount = 0;
        int copiedLegacyBackupCount = 0;
        int addedCharacterCount = 0;

        foreach (string relativePath in new[]
                 {
                     "characters.json",
                     "deck.json",
                     "deck_presets.json",
                     "gaccag_update.json",
                     "gaccag_words.json.gz",
                     "gaccag_words.json"
                 })
        {
            string sourcePath = Path.Combine(BundledDataDirectory, relativePath);
            string destinationPath = Path.Combine(UserDataDirectory, relativePath);
            if (!File.Exists(destinationPath) && File.Exists(sourcePath))
            {
                CopyFileAtomically(sourcePath, destinationPath);
                copiedFileCount++;
            }
        }

        string bundledManifestPath = Path.Combine(BundledDataDirectory, "data_manifest.json");
        string userManifestPath = Path.Combine(UserDataDirectory, "data_manifest.json");
        if (!File.Exists(userManifestPath))
        {
            if (createdUserData && File.Exists(bundledManifestPath))
            {
                CopyFileAtomically(bundledManifestPath, userManifestPath);
                copiedFileCount++;
            }
            else
            {
                // v1.25.0 이하에서 올라온 기존 사용자는 데이터 버전 파일이 없었습니다.
                // 최신 번들 버전으로 잘못 표시하지 않고 업데이트 체인의 최초 기준으로 기록합니다.
                WriteUserDataVersionManifest(userManifestPath, InitialDataVersion);
                copiedFileCount++;
            }
        }

        string bundledReferences = Path.Combine(BundledDataDirectory, "RecognitionReferences");
        string userReferences = Path.Combine(UserDataDirectory, "RecognitionReferences");
        if (Directory.Exists(bundledReferences))
        {
            copiedReferenceFileCount = CopyMissingDirectoryFiles(
                bundledReferences,
                userReferences);
        }

        string? bundledProjectDirectory = Path.GetDirectoryName(BundledDataDirectory);
        if (!string.IsNullOrWhiteSpace(bundledProjectDirectory))
        {
            string legacyBackupDirectory = Path.Combine(bundledProjectDirectory, "Backups");
            if (Directory.Exists(legacyBackupDirectory) &&
                !string.Equals(
                    Path.GetFullPath(legacyBackupDirectory),
                    Path.GetFullPath(BackupDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                copiedLegacyBackupCount = CopyMissingBackupFiles(
                    legacyBackupDirectory,
                    BackupDirectory);
            }
        }

        string bundledCharactersPath = Path.Combine(BundledDataDirectory, "characters.json");
        string userCharactersPath = Path.Combine(UserDataDirectory, "characters.json");
        HashSet<string> previouslyBundledCharacterIds = LoadPreviouslyBundledCharacterIds();
        IReadOnlyList<string> currentBundledCharacterIds = Array.Empty<string>();
        if (File.Exists(bundledCharactersPath))
        {
            currentBundledCharacterIds = JsonDataLoader.LoadCharacters(bundledCharactersPath)
                .Select(character => character.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
        }

        if (File.Exists(bundledCharactersPath) && File.Exists(userCharactersPath))
        {
            addedCharacterCount = MergeMissingBundledCharacters(
                bundledCharactersPath,
                userCharactersPath,
                previouslyBundledCharacterIds,
                forceRestoreMissingBundledCharacters);
        }

        BundledDataVersion = ReadDataVersion(
            Path.Combine(BundledDataDirectory, "data_manifest.json"),
            DefaultDataVersion);
        UserDataVersion = ReadDataVersion(
            Path.Combine(UserDataDirectory, "data_manifest.json"),
            BundledDataVersion);

        WriteDataState(
            createdUserData,
            copiedFileCount,
            copiedReferenceFileCount,
            copiedLegacyBackupCount,
            addedCharacterCount,
            currentBundledCharacterIds);

        return new DataInitializationResult
        {
            CreatedUserData = createdUserData,
            CopiedFileCount = copiedFileCount,
            CopiedReferenceFileCount = copiedReferenceFileCount,
            CopiedLegacyBackupCount = copiedLegacyBackupCount,
            AddedBundledCharacterCount = addedCharacterCount,
            BundledDataDirectory = BundledDataDirectory,
            UserDataDirectory = UserDataDirectory
        };
    }

    private static int MergeMissingBundledCharacters(
        string bundledCharactersPath,
        string userCharactersPath,
        IReadOnlySet<string> previouslyBundledCharacterIds,
        bool forceRestoreMissingBundledCharacters)
    {
        IReadOnlyList<CharacterEntry> bundledCharacters =
            JsonDataLoader.LoadCharacters(bundledCharactersPath);
        IReadOnlyList<CharacterEntry> userCharacters =
            JsonDataLoader.LoadCharacters(userCharactersPath);

        var userIds = userCharacters
            .Where(character => !string.IsNullOrWhiteSpace(character.Id))
            .Select(character => character.Id)
            .ToHashSet(StringComparer.Ordinal);

        CharacterEntry[] missingCharacters = bundledCharacters
            .Where(character =>
                !string.IsNullOrWhiteSpace(character.Id) &&
                !userIds.Contains(character.Id) &&
                (forceRestoreMissingBundledCharacters ||
                 previouslyBundledCharacterIds.Count == 0 ||
                 !previouslyBundledCharacterIds.Contains(character.Id)))
            .Select(CharacterLibraryService.Clone)
            .ToArray();

        if (missingCharacters.Length == 0)
        {
            return 0;
        }

        CharacterEntry[] merged = userCharacters
            .Select(CharacterLibraryService.Clone)
            .Concat(missingCharacters)
            .OrderBy(character => character.Name, StringComparer.Ordinal)
            .ThenBy(character => character.Id, StringComparer.Ordinal)
            .ToArray();

        DeckDataService.Save(userCharactersPath, merged);
        return missingCharacters.Length;
    }

    private static string? FindBundledDataDirectory()
    {
        foreach (string startDirectory in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            try
            {
                DirectoryInfo? directory = new(Path.GetFullPath(startDirectory));
                for (int depth = 0; depth < 7 && directory is not null; depth++, directory = directory.Parent)
                {
                    // 배포본은 실행 파일 옆 Data만 씁니다(depth 0).
                    // 상위 폴더 탐색은 프로젝트 파일이 함께 있는 개발 트리에서만 허용해,
                    // 배포본의 Data가 사라졌을 때 무관한 상위 폴더의 Data를 집는 것을 막습니다.
                    if (depth > 0 &&
                        !File.Exists(Path.Combine(directory.FullName, ProductName + ".csproj")))
                    {
                        continue;
                    }

                    string candidate = Path.Combine(directory.FullName, "Data");
                    if (Directory.Exists(candidate) &&
                        File.Exists(Path.Combine(candidate, "characters.json")) &&
                        (File.Exists(Path.Combine(candidate, "words.json")) ||
                         File.Exists(Path.Combine(candidate, "words.txt"))))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
            }
            catch
            {
                // 다음 시작 경로를 확인합니다.
            }
        }

        return null;
    }

    private static void CopyFileAtomically(string sourcePath, string destinationPath)
    {
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        string temporaryPath = destinationPath + ".seed.tmp";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: true);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        finally
        {
            // Move가 실패하면 임시 파일이 사용자 Data 폴더에 남습니다.
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static int CopyMissingDirectoryFiles(string sourceDirectory, string destinationDirectory)
    {
        int copiedCount = 0;
        foreach (string sourcePath in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*",
                     SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            if (File.Exists(destinationPath))
            {
                continue;
            }

            CopyFileAtomically(sourcePath, destinationPath);
            copiedCount++;
        }

        return copiedCount;
    }

    private static HashSet<string> LoadPreviouslyBundledCharacterIds()
    {
        try
        {
            if (!File.Exists(DataStatePath))
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(DataStatePath));
            if (!document.RootElement.TryGetProperty("BundledCharacterIds", out JsonElement idsElement) ||
                idsElement.ValueKind != JsonValueKind.Array)
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            return idsElement
                .EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);
        }
        catch
        {
            // 상태 파일이 손상됐으면 안전하게 첫 동기화처럼 처리합니다.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static int CopyMissingBackupFiles(
        string sourceDirectory,
        string destinationDirectory)
    {
        int copiedCount = 0;
        Directory.CreateDirectory(destinationDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*.zip",
                     SearchOption.TopDirectoryOnly))
        {
            string destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourcePath));
            if (File.Exists(destinationPath))
            {
                continue;
            }

            CopyFileAtomically(sourcePath, destinationPath);
            copiedCount++;
        }

        return copiedCount;
    }


    private static void WriteUserDataVersionManifest(string path, string dataVersion)
    {
        int characterCount = 0;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            string charactersPath = Path.Combine(directory ?? UserDataDirectory, "characters.json");
            if (File.Exists(charactersPath))
            {
                characterCount = JsonDataLoader.LoadCharacters(charactersPath).Count;
            }
        }
        catch
        {
            // 버전 메타데이터의 개수 표시는 보조 정보이므로 실패해도 계속합니다.
        }

        var metadata = new
        {
            SchemaVersion = 1,
            DataVersion = dataVersion,
            MinimumAppVersion = AppVersion,
            UpdatedAt = DateTimeOffset.Now,
            CharacterCount = characterCount
        };

        string json = JsonSerializer.Serialize(
            metadata,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private static string ReadDataVersion(string path, string fallback)
    {
        try
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("DataVersion", out JsonElement versionElement) &&
                versionElement.ValueKind == JsonValueKind.String)
            {
                string? version = versionElement.GetString();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version.Trim();
                }
            }
        }
        catch
        {
            // 버전 파일이 손상돼도 기본 데이터 로딩은 계속합니다.
        }

        return fallback;
    }

    private static void WriteDataState(
        bool createdUserData,
        int copiedFileCount,
        int copiedReferenceFileCount,
        int copiedLegacyBackupCount,
        int addedCharacterCount,
        IReadOnlyList<string> bundledCharacterIds)
    {
        var state = new
        {
            App = ProductName,
            AppVersion,
            BundledDataVersion,
            UserDataVersion,
            LastSynchronizedAt = DateTimeOffset.Now,
            CreatedUserData = createdUserData,
            CopiedFileCount = copiedFileCount,
            CopiedReferenceFileCount = copiedReferenceFileCount,
            CopiedLegacyBackupCount = copiedLegacyBackupCount,
            AddedBundledCharacterCount = addedCharacterCount,
            BundledCharacterIds = bundledCharacterIds,
            BundledDataDirectory,
            UserDataDirectory
        };

        string temporaryPath = DataStatePath + ".tmp";
        string json = JsonSerializer.Serialize(
            state,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, DataStatePath, overwrite: true);
    }
}

public sealed class DataInitializationResult
{
    public bool CreatedUserData { get; init; }
    public int CopiedFileCount { get; init; }
    public int CopiedReferenceFileCount { get; init; }
    public int CopiedLegacyBackupCount { get; init; }
    public int AddedBundledCharacterCount { get; init; }
    public string BundledDataDirectory { get; init; } = string.Empty;
    public string UserDataDirectory { get; init; } = string.Empty;
}
