using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KotodamanWordFinder.Models;

namespace KotodamanWordFinder.Services;

/// <summary>
/// 캐릭터/이미지 데이터 업데이트 ZIP을 검증하고 사용자 데이터에 안전하게 병합합니다.
/// 기존 사용자가 수정한 필드는 3-way merge로 보존하며, 적용 전에 전체 사용자 데이터를 백업합니다.
/// </summary>
public static class DataUpdateService
{
    public const int SupportedSchemaVersion = 1;
    public const string SupportedPackageType = "KotodamanDataUpdate";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static DataUpdatePackageInfo InspectPackage(string packagePath)
    {
        string fullPackagePath = ValidatePackagePath(packagePath);
        using ZipArchive archive = ZipFile.OpenRead(fullPackagePath);
        DataUpdateManifest manifest = ReadRequiredJson<DataUpdateManifest>(archive, "manifest.json");
        IReadOnlyList<DataUpdateCharacterChange> changes =
            ReadRequiredJson<List<DataUpdateCharacterChange>>(archive, "characters_delta.json");

        ValidateManifest(manifest, changes, archive);
        return new DataUpdatePackageInfo(fullPackagePath, manifest, changes.Count);
    }

    public static BundledDataUpdateResult ApplyBundledUpdates()
    {
        AppPaths.Initialize();

        string updateDirectory = Path.Combine(AppPaths.BundledDataDirectory, "BundledUpdates");
        if (!Directory.Exists(updateDirectory))
        {
            return new BundledDataUpdateResult
            {
                InitialDataVersion = AppPaths.UserDataVersion,
                FinalDataVersion = AppPaths.UserDataVersion
            };
        }

        var packages = new List<DataUpdatePackageInfo>();
        foreach (string path in Directory.EnumerateFiles(
                     updateDirectory,
                     "*.zip",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                packages.Add(InspectPackage(path));
            }
            catch (Exception exception)
            {
                AppLog.Warning($"번들 데이터 업데이트를 검사하지 못했습니다: {Path.GetFileName(path)} · {exception.Message}");
            }
        }

        string initialVersion = AppPaths.UserDataVersion;
        string currentVersion = initialVersion;
        var appliedPackages = new List<string>();
        int addedCount = 0;
        int updatedCount = 0;
        int deletedCount = 0;
        int imageCount = 0;
        int referenceCount = 0;
        int conflictCount = 0;
        var errors = new List<string>();

        // packages는 적용할 때마다 줄어들므로 상한을 미리 고정합니다.
        // Count를 매 반복 다시 읽으면 체인이 3개 이상일 때 마지막 패키지를 건너뜁니다.
        int maximumSteps = packages.Count;
        for (int safety = 0; safety < maximumSteps; safety++)
        {
            DataUpdatePackageInfo? next = packages
                .Where(package => string.Equals(
                    package.Manifest.FromDataVersion,
                    currentVersion,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(package => package.Manifest.DataVersion, DataVersionComparer.Instance)
                .FirstOrDefault();

            if (next is null)
            {
                break;
            }

            try
            {
                DataUpdateApplyResult result = ApplyPackage(next.PackagePath);
                appliedPackages.Add(Path.GetFileName(next.PackagePath));
                currentVersion = result.DataVersion;
                addedCount += result.AddedCharacterCount;
                updatedCount += result.UpdatedCharacterCount;
                deletedCount += result.DeletedCharacterCount;
                imageCount += result.AppliedImageCount;
                referenceCount += result.AppliedReferenceCount;
                conflictCount += result.PreservedConflictCount;
                packages.Remove(next);
            }
            catch (Exception exception)
            {
                string error = $"{Path.GetFileName(next.PackagePath)}: {exception.Message}";
                errors.Add(error);
                AppLog.Error("번들 데이터 업데이트 자동 적용에 실패했습니다.", exception);
                break;
            }
        }

        AppPaths.RefreshUserDataVersion();
        return new BundledDataUpdateResult
        {
            InitialDataVersion = initialVersion,
            FinalDataVersion = AppPaths.UserDataVersion,
            AppliedPackages = appliedPackages,
            AddedCharacterCount = addedCount,
            UpdatedCharacterCount = updatedCount,
            DeletedCharacterCount = deletedCount,
            AppliedImageCount = imageCount,
            AppliedReferenceCount = referenceCount,
            PreservedConflictCount = conflictCount,
            Errors = errors
        };
    }

    public static DataUpdateApplyResult ApplyPackage(string packagePath)
    {
        AppPaths.Initialize();

        string fullPackagePath = ValidatePackagePath(packagePath);
        using ZipArchive archive = ZipFile.OpenRead(fullPackagePath);
        DataUpdateManifest manifest = ReadRequiredJson<DataUpdateManifest>(archive, "manifest.json");
        List<DataUpdateCharacterChange> changes =
            ReadRequiredJson<List<DataUpdateCharacterChange>>(archive, "characters_delta.json");

        ValidateManifest(manifest, changes, archive);

        string backupPath = DataBackupService.CreateManualBackup(AppPaths.UserDataDirectory);
        string charactersPath = AppPaths.GetUserDataPath("characters.json");
        IReadOnlyList<CharacterEntry> loadedCharacters = JsonDataLoader.LoadCharacters(charactersPath);
        var charactersById = loadedCharacters
            .Where(character => !string.IsNullOrWhiteSpace(character.Id))
            .GroupBy(character => character.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => CharacterLibraryService.Clone(group.First()),
                StringComparer.Ordinal);

        int addedCount = 0;
        int updatedCount = 0;
        int deletedCount = 0;
        int characterConflictCount = 0;

        foreach (DataUpdateCharacterChange change in changes)
        {
            string id = (change.Id ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                throw new InvalidDataException("데이터 업데이트에 ID가 비어 있는 캐릭터 변경이 있습니다.");
            }

            switch (NormalizeChangeType(change.ChangeType))
            {
                case "Add":
                    if (change.Current is null)
                    {
                        throw new InvalidDataException($"추가 캐릭터의 Current 데이터가 없습니다: {id}");
                    }

                    if (!charactersById.ContainsKey(id))
                    {
                        charactersById[id] = CharacterLibraryService.Clone(change.Current);
                        addedCount++;
                    }
                    else
                    {
                        characterConflictCount++;
                    }
                    break;

                case "Update":
                    if (change.Previous is null || change.Current is null)
                    {
                        throw new InvalidDataException($"수정 캐릭터의 Previous/Current 데이터가 없습니다: {id}");
                    }

                    if (!charactersById.TryGetValue(id, out CharacterEntry? userCharacter))
                    {
                        // 사용자가 의도적으로 지운 캐릭터는 자동 복구하지 않습니다.
                        characterConflictCount++;
                        break;
                    }

                    int mergeConflictCount = 0;
                    CharacterEntry merged = MergeCharacter(
                        change.Previous,
                        change.Current,
                        userCharacter,
                        ref mergeConflictCount);
                    charactersById[id] = merged;
                    updatedCount++;
                    characterConflictCount += mergeConflictCount;
                    break;

                case "Delete":
                    if (change.Previous is null)
                    {
                        throw new InvalidDataException($"삭제 캐릭터의 Previous 데이터가 없습니다: {id}");
                    }

                    if (!charactersById.TryGetValue(id, out CharacterEntry? candidate))
                    {
                        break;
                    }

                    if (AreEquivalent(candidate, change.Previous))
                    {
                        charactersById.Remove(id);
                        deletedCount++;
                    }
                    else
                    {
                        // 사용자가 수정한 캐릭터는 공식 데이터에서 삭제됐더라도 보존합니다.
                        characterConflictCount++;
                    }
                    break;

                default:
                    throw new InvalidDataException($"알 수 없는 캐릭터 변경 종류입니다: {change.ChangeType}");
            }
        }

        CharacterEntry[] mergedCharacters = charactersById.Values
            .OrderBy(character => character.Name, StringComparer.Ordinal)
            .ThenBy(character => character.Id, StringComparer.Ordinal)
            .ToArray();

        if (mergedCharacters.Length == 0)
        {
            throw new InvalidDataException("업데이트 적용 결과 캐릭터가 한 명도 남지 않아 저장을 중단했습니다.");
        }

        // 캐릭터 데이터를 먼저 확정합니다. 이미지 적용이 중간에 실패해도 캐릭터 정보는 온전하고,
        // 데이터 버전은 아래에서만 올라가므로 같은 패키지를 다시 적용해 이미지만 따라잡을 수 있습니다.
        DeckDataService.Save(charactersPath, mergedCharacters);

        int appliedImageCount = 0;
        int imageConflictCount = 0;
        ApplyImageChanges(archive, manifest.Images, ref appliedImageCount, ref imageConflictCount);

        int appliedReferenceCount = 0;
        int referenceConflictCount = 0;
        ApplyReferenceChanges(archive, manifest.References, ref appliedReferenceCount, ref referenceConflictCount);

        WriteAppliedDataVersion(manifest, mergedCharacters.Length, fullPackagePath);
        AppPaths.RefreshUserDataVersion();

        int totalConflictCount = characterConflictCount + imageConflictCount + referenceConflictCount;
        AppLog.Info(
            $"데이터 업데이트 적용 완료 · {manifest.FromDataVersion} → {manifest.DataVersion} · " +
            $"캐릭터 추가={addedCount}, 수정={updatedCount}, 삭제={deletedCount}, " +
            $"이미지={appliedImageCount}, 학습참조={appliedReferenceCount}, 충돌보존={totalConflictCount}");

        return new DataUpdateApplyResult
        {
            FromDataVersion = manifest.FromDataVersion,
            DataVersion = manifest.DataVersion,
            AddedCharacterCount = addedCount,
            UpdatedCharacterCount = updatedCount,
            DeletedCharacterCount = deletedCount,
            AppliedImageCount = appliedImageCount,
            AppliedReferenceCount = appliedReferenceCount,
            PreservedConflictCount = totalConflictCount,
            BackupPath = backupPath,
            PackagePath = fullPackagePath
        };
    }

    private static void ValidateManifest(
        DataUpdateManifest manifest,
        IReadOnlyList<DataUpdateCharacterChange> changes,
        ZipArchive archive)
    {
        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"지원하지 않는 데이터 업데이트 형식입니다. " +
                $"패키지={manifest.SchemaVersion}, 지원={SupportedSchemaVersion}");
        }

        if (!string.Equals(
                manifest.PackageType,
                SupportedPackageType,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("코토다망 데이터 업데이트 패키지가 아닙니다.");
        }

        if (string.IsNullOrWhiteSpace(manifest.DataVersion))
        {
            throw new InvalidDataException("데이터 버전이 비어 있습니다.");
        }

        manifest.Images ??= new List<DataUpdateImageChange>();

        if (Version.TryParse(manifest.MinimumAppVersion, out Version? minimumVersion) &&
            Version.TryParse(AppPaths.AppVersion, out Version? currentVersion) &&
            currentVersion < minimumVersion)
        {
            throw new InvalidOperationException(
                $"이 데이터는 프로그램 v{manifest.MinimumAppVersion} 이상이 필요합니다. " +
                $"현재 프로그램은 v{AppPaths.AppVersion}입니다.");
        }

        var duplicateChange = changes
            .Where(change => !string.IsNullOrWhiteSpace(change.Id))
            .GroupBy(change => change.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateChange is not null)
        {
            throw new InvalidDataException($"중복 캐릭터 변경 ID가 있습니다: {duplicateChange.Key}");
        }

        int addCount = changes.Count(change => NormalizeChangeType(change.ChangeType) == "Add");
        int updateCount = changes.Count(change => NormalizeChangeType(change.ChangeType) == "Update");
        int deleteCount = changes.Count(change => NormalizeChangeType(change.ChangeType) == "Delete");
        if (addCount != manifest.AddedCharacterCount ||
            updateCount != manifest.UpdatedCharacterCount ||
            deleteCount != manifest.DeletedCharacterCount)
        {
            throw new InvalidDataException("manifest.json의 캐릭터 변경 수와 실제 변경 파일이 일치하지 않습니다.");
        }

        foreach (DataUpdateImageChange image in manifest.Images)
        {
            string fileName = Path.GetFileName(image.FileName ?? string.Empty);
            if (fileName.Length == 0 ||
                !string.Equals(fileName, image.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"잘못된 이미지 파일 이름입니다: {image.FileName}");
            }

            string changeType = NormalizeChangeType(image.ChangeType);
            if (changeType is "Add" or "Update")
            {
                ZipArchiveEntry? entry = FindEntry(archive, image.ArchivePath);
                if (entry is null)
                {
                    throw new InvalidDataException($"패키지에 이미지가 없습니다: {image.ArchivePath}");
                }
            }
        }

        manifest.References ??= new List<DataUpdateReferenceChange>();

        foreach (DataUpdateReferenceChange reference in manifest.References)
        {
            string relativePath = NormalizeRelativePath(reference.RelativePath ?? string.Empty);
            if (!IsSafeRelativePath(relativePath))
            {
                throw new InvalidDataException($"잘못된 학습 참조 경로입니다: {reference.RelativePath}");
            }

            string changeType = NormalizeChangeType(reference.ChangeType);
            if (changeType is "Add" or "Update")
            {
                ZipArchiveEntry? entry = FindEntry(archive, reference.ArchivePath);
                if (entry is null)
                {
                    throw new InvalidDataException($"패키지에 학습 참조 파일이 없습니다: {reference.ArchivePath}");
                }
            }
        }
    }

    private static CharacterEntry MergeCharacter(
        CharacterEntry previous,
        CharacterEntry current,
        CharacterEntry user,
        ref int conflictCount)
    {
        JsonNode? previousNode = JsonSerializer.SerializeToNode(previous, JsonOptions);
        JsonNode? currentNode = JsonSerializer.SerializeToNode(current, JsonOptions);
        JsonNode? userNode = JsonSerializer.SerializeToNode(user, JsonOptions);
        JsonNode? mergedNode = MergeNode(previousNode, currentNode, userNode, ref conflictCount);
        CharacterEntry? merged = mergedNode?.Deserialize<CharacterEntry>(JsonOptions);
        return merged is null
            ? CharacterLibraryService.Clone(user)
            : CharacterLibraryService.Clone(merged);
    }

    private static JsonNode? MergeNode(
        JsonNode? previous,
        JsonNode? current,
        JsonNode? user,
        ref int conflictCount)
    {
        if (JsonNode.DeepEquals(user, previous))
        {
            return current?.DeepClone();
        }

        if (JsonNode.DeepEquals(current, previous))
        {
            return user?.DeepClone();
        }

        if (previous is JsonObject previousObject &&
            current is JsonObject currentObject &&
            user is JsonObject userObject)
        {
            var merged = new JsonObject();
            var names = previousObject.Select(pair => pair.Key)
                .Concat(currentObject.Select(pair => pair.Key))
                .Concat(userObject.Select(pair => pair.Key))
                .Distinct(StringComparer.Ordinal);

            foreach (string name in names)
            {
                bool hasPrevious = previousObject.TryGetPropertyValue(name, out JsonNode? previousValue);
                bool hasCurrent = currentObject.TryGetPropertyValue(name, out JsonNode? currentValue);
                bool hasUser = userObject.TryGetPropertyValue(name, out JsonNode? userValue);

                if (!hasPrevious)
                {
                    if (!hasUser && hasCurrent)
                    {
                        merged[name] = currentValue?.DeepClone();
                    }
                    else if (hasUser)
                    {
                        merged[name] = userValue?.DeepClone();
                        if (hasCurrent && !JsonNode.DeepEquals(userValue, currentValue))
                        {
                            conflictCount++;
                        }
                    }
                    continue;
                }

                if (!hasCurrent)
                {
                    if (hasUser && !JsonNode.DeepEquals(userValue, previousValue))
                    {
                        merged[name] = userValue?.DeepClone();
                        conflictCount++;
                    }
                    continue;
                }

                if (!hasUser)
                {
                    if (!JsonNode.DeepEquals(currentValue, previousValue))
                    {
                        // 사용자가 이 속성을 지운 상태라면 삭제 의도를 보존합니다.
                        conflictCount++;
                    }
                    continue;
                }

                merged[name] = MergeNode(
                    previousValue,
                    currentValue,
                    userValue,
                    ref conflictCount);
            }

            return merged;
        }

        // 배열과 단일 값은 하나의 단위로 취급합니다. 사용자가 수정했다면 사용자 값을 보존합니다.
        conflictCount++;
        return user?.DeepClone();
    }

    private static void ApplyImageChanges(
        ZipArchive archive,
        IReadOnlyList<DataUpdateImageChange> images,
        ref int appliedCount,
        ref int conflictCount)
    {
        ApplyFileChanges(
            archive,
            AppPaths.UserImageDirectory,
            Path.Combine(AppPaths.BundledDataDirectory, "CharacterImages"),
            images.Select(image => new PackagedFileChange(
                Path.GetFileName(image.FileName),
                NormalizeChangeType(image.ChangeType),
                image.ArchivePath,
                image.PreviousSha256,
                image.CurrentSha256)),
            "이미지",
            ref appliedCount,
            ref conflictCount);
    }

    // DeckScreenshotLearningService가 "<UI 프로필>/<캐릭터>/slot-*.png" 구조로 쓰는
    // 인식 학습 참조 이미지입니다. 이미지와 같은 해시 검증·충돌 보존 규칙을 그대로 씁니다.
    private static void ApplyReferenceChanges(
        ZipArchive archive,
        IReadOnlyList<DataUpdateReferenceChange> references,
        ref int appliedCount,
        ref int conflictCount)
    {
        ApplyFileChanges(
            archive,
            Path.Combine(AppPaths.UserDataDirectory, "RecognitionReferences"),
            Path.Combine(AppPaths.BundledDataDirectory, "RecognitionReferences"),
            references.Select(reference => new PackagedFileChange(
                NormalizeRelativePath(reference.RelativePath),
                NormalizeChangeType(reference.ChangeType),
                reference.ArchivePath,
                reference.PreviousSha256,
                reference.CurrentSha256)),
            "학습 참조 파일",
            ref appliedCount,
            ref conflictCount);
    }

    private readonly record struct PackagedFileChange(
        string RelativePath,
        string ChangeType,
        string ArchivePath,
        string? PreviousSha256,
        string? CurrentSha256);

    // 사용자 폴더 아래 상대 경로 하나로 식별되는 파일을 해시로 검증하며 추가/수정/삭제합니다.
    // CharacterImages(평평한 파일명)와 RecognitionReferences(중첩 폴더) 양쪽에서 씁니다.
    private static void ApplyFileChanges(
        ZipArchive archive,
        string userRootDirectory,
        string bundledRootDirectory,
        IEnumerable<PackagedFileChange> changes,
        string labelForErrors,
        ref int appliedCount,
        ref int conflictCount)
    {
        Directory.CreateDirectory(userRootDirectory);

        foreach (PackagedFileChange change in changes)
        {
            string userPath = Path.Combine(userRootDirectory, change.RelativePath);
            string bundledPath = Path.Combine(bundledRootDirectory, change.RelativePath);
            string? effectivePath = File.Exists(userPath)
                ? userPath
                : File.Exists(bundledPath)
                    ? bundledPath
                    : null;
            string? effectiveHash = effectivePath is null ? null : ComputeSha256(effectivePath);

            if (change.ChangeType == "Delete")
            {
                if (File.Exists(userPath) &&
                    HashEquals(effectiveHash, change.PreviousSha256))
                {
                    File.Delete(userPath);
                    appliedCount++;
                }
                else if (File.Exists(userPath))
                {
                    conflictCount++;
                }
                continue;
            }

            if (HashEquals(effectiveHash, change.CurrentSha256))
            {
                continue;
            }

            bool canReplace = effectivePath is null ||
                              (change.ChangeType == "Update" &&
                               HashEquals(effectiveHash, change.PreviousSha256));
            if (!canReplace)
            {
                conflictCount++;
                continue;
            }

            ZipArchiveEntry entry = FindEntry(archive, change.ArchivePath)
                ?? throw new InvalidDataException($"패키지에 {labelForErrors}이 없습니다: {change.ArchivePath}");

            string? destinationDirectory = Path.GetDirectoryName(userPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            string temporaryPath = userPath + ".update.tmp";
            try
            {
                using (Stream input = entry.Open())
                using (var output = new FileStream(
                           temporaryPath,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None))
                {
                    input.CopyTo(output);
                }

                string downloadedHash = ComputeSha256(temporaryPath);
                if (!HashEquals(downloadedHash, change.CurrentSha256))
                {
                    throw new InvalidDataException($"{labelForErrors} 해시가 일치하지 않습니다: {change.RelativePath}");
                }

                File.Move(temporaryPath, userPath, overwrite: true);
                appliedCount++;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }

    private static void WriteAppliedDataVersion(
        DataUpdateManifest manifest,
        int characterCount,
        string packagePath)
    {
        var metadata = new DataVersionMetadata
        {
            SchemaVersion = 1,
            DataVersion = manifest.DataVersion,
            MinimumAppVersion = manifest.MinimumAppVersion,
            UpdatedAt = DateTimeOffset.Now,
            CharacterCount = characterCount
        };

        string path = AppPaths.GetUserDataPath("data_manifest.json");
        string temporaryPath = path + ".tmp";
        string json = JsonSerializer.Serialize(metadata, JsonOptions);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);

        string statePath = Path.Combine(AppPaths.UserRootDirectory, "last_data_update.json");
        string stateJson = JsonSerializer.Serialize(
            new
            {
                manifest.FromDataVersion,
                manifest.DataVersion,
                AppliedAt = DateTimeOffset.Now,
                PackagePath = packagePath
            },
            JsonOptions);
        File.WriteAllText(statePath, stateJson, new UTF8Encoding(false));
    }

    private static T ReadRequiredJson<T>(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"업데이트 패키지에 {entryName} 파일이 없습니다.");
        using Stream stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
               ?? throw new InvalidDataException($"{entryName} 파일을 읽지 못했습니다.");
    }

    private static string ValidatePackagePath(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("데이터 업데이트 ZIP 경로가 비어 있습니다.", nameof(packagePath));
        }

        string fullPath = Path.GetFullPath(packagePath.Trim().Trim('"'));
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("데이터 업데이트 ZIP을 찾을 수 없습니다.", fullPath);
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("데이터 업데이트 파일은 ZIP 형식이어야 합니다.");
        }

        return fullPath;
    }

    private static bool AreEquivalent(CharacterEntry left, CharacterEntry right)
        => JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(left, JsonOptions),
            JsonSerializer.SerializeToNode(right, JsonOptions));

    private static string NormalizeChangeType(string? value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (normalized.Equals("Add", StringComparison.OrdinalIgnoreCase)) return "Add";
        if (normalized.Equals("Update", StringComparison.OrdinalIgnoreCase)) return "Update";
        if (normalized.Equals("Delete", StringComparison.OrdinalIgnoreCase)) return "Delete";
        return normalized;
    }

    private static string NormalizeArchivePath(string? value)
        => (value ?? string.Empty).Replace('\\', '/').TrimStart('/');

    // Windows PowerShell 5.1(.NET Framework)의 Compress-Archive는 중첩 폴더가 있는
    // 항목 이름에 '\'를 그대로 씁니다(ZIP 규격은 '/'). archive.GetEntry는 정확히 일치하는
    // 이름만 찾으므로 어떤 도구로 만든 zip이든 구분자와 무관하게 찾도록 폴백합니다.
    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string? archivePath)
    {
        string normalized = NormalizeArchivePath(archivePath);
        return archive.GetEntry(normalized) ??
               archive.Entries.FirstOrDefault(entry =>
                   string.Equals(NormalizeArchivePath(entry.FullName), normalized, StringComparison.Ordinal));
    }

    private static string NormalizeRelativePath(string? value)
        => (value ?? string.Empty).Replace('\\', '/').Trim('/');

    // Path.Combine(userRootDirectory, relativePath)가 루트 밖으로 못 벗어나도록
    // 각 경로 조각을 검사합니다(zip slip 방어). 이미지는 파일명 하나뿐이라 필요 없었지만
    // 학습 참조 파일은 중첩 폴더 구조라 ".."·드라이브 루트 조각을 걸러야 합니다.
    private static bool IsSafeRelativePath(string relativePath)
    {
        if (relativePath.Length == 0 || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        foreach (string segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 ||
                segment == "." ||
                segment == ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool HashEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

internal sealed class DataVersionComparer : IComparer<string>
{
    public static DataVersionComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (Version.TryParse(left, out Version? leftVersion) &&
            Version.TryParse(right, out Version? rightVersion))
        {
            return leftVersion.CompareTo(rightVersion);
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class BundledDataUpdateResult
{
    public string InitialDataVersion { get; init; } = string.Empty;
    public string FinalDataVersion { get; init; } = string.Empty;
    public IReadOnlyList<string> AppliedPackages { get; init; } = Array.Empty<string>();
    public int AddedCharacterCount { get; init; }
    public int UpdatedCharacterCount { get; init; }
    public int DeletedCharacterCount { get; init; }
    public int AppliedImageCount { get; init; }
    public int AppliedReferenceCount { get; init; }
    public int PreservedConflictCount { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public bool HasAppliedUpdates => AppliedPackages.Count > 0;
    public bool HasErrors => Errors.Count > 0;
}

public sealed record DataUpdatePackageInfo(
    string PackagePath,
    DataUpdateManifest Manifest,
    int CharacterChangeCount);

public sealed class DataUpdateApplyResult
{
    public string FromDataVersion { get; init; } = string.Empty;
    public string DataVersion { get; init; } = string.Empty;
    public int AddedCharacterCount { get; init; }
    public int UpdatedCharacterCount { get; init; }
    public int DeletedCharacterCount { get; init; }
    public int AppliedImageCount { get; init; }
    public int AppliedReferenceCount { get; init; }
    public int PreservedConflictCount { get; init; }
    public string BackupPath { get; init; } = string.Empty;
    public string PackagePath { get; init; } = string.Empty;
}
