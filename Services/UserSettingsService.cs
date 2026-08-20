using System.IO;
using System.Text.Json;
using KotodamanWordFinder.Models;

namespace KotodamanWordFinder.Services;

public static class UserSettingsService
{
    private static readonly object SyncRoot = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static UserSettings? _cachedSettings;

    private static string SettingsDirectory => AppPaths.UserRootDirectory;

    private static string SettingsPath => AppPaths.SettingsPath;

    public static UserSettings Load()
    {
        lock (SyncRoot)
        {
            _cachedSettings ??= LoadFromDisk();
            return Clone(_cachedSettings);
        }
    }

    public static void Save(UserSettings settings)
    {
        lock (SyncRoot)
        {
            UserSettings snapshot = Clone(settings);
            SaveToDisk(snapshot);
            _cachedSettings = snapshot;
        }
    }

    public static void Update(Action<UserSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        lock (SyncRoot)
        {
            _cachedSettings ??= LoadFromDisk();
            UserSettings working = Clone(_cachedSettings);
            update(working);
            SaveToDisk(working);
            _cachedSettings = working;
        }
    }


    public static void InvalidateCache()
    {
        lock (SyncRoot)
        {
            _cachedSettings = null;
        }
    }

    public static void SaveLastDeckEditorCharacterId(string? characterId)
    {
        try
        {
            Update(settings =>
                settings.LastDeckEditorCharacterId = characterId?.Trim() ?? string.Empty);
        }
        catch
        {
            // 마지막 선택 복원 실패가 덱 편집을 막지는 않게 합니다.
        }
    }

    public static void SaveLastDeckEditorGroupFilter(string? groupFilter)
    {
        try
        {
            Update(settings =>
                settings.LastDeckEditorGroupFilter = string.IsNullOrWhiteSpace(groupFilter)
                    ? "전체 그룹"
                    : groupFilter.Trim());
        }
        catch
        {
            // 마지막 그룹 필터 복원 실패가 덱 편집을 막지는 않게 합니다.
        }
    }

    private static UserSettings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new UserSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    private static void SaveToDisk(UserSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        string json = JsonSerializer.Serialize(settings, JsonOptions);

        // 설정 저장 도중 앱이 종료되어도 기존 파일이 망가지지 않도록 임시 파일 후 교체합니다.
        string temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, SettingsPath, overwrite: true);
    }

    private static UserSettings Clone(UserSettings source)
        => new()
        {
            SelectedHandCharacterIds = (source.SelectedHandCharacterIds ?? new List<string>()).ToList(),
            SelectedHandLetterStateIds = new Dictionary<string, string>(
                source.SelectedHandLetterStateIds ?? new Dictionary<string, string>(),
                StringComparer.Ordinal),
            SelectedHandFormIds = new Dictionary<string, string>(
                source.SelectedHandFormIds ?? new Dictionary<string, string>(),
                StringComparer.Ordinal),
            BoardCells = (source.BoardCells ?? new List<string?>()).ToList(),
            AutoSearchEnabled = source.AutoSearchEnabled,
            DeckResultSortMode = source.DeckResultSortMode ?? "Practical",
            LastDeckEditorCharacterId = source.LastDeckEditorCharacterId ?? string.Empty,
            LastDeckEditorSearchText = source.LastDeckEditorSearchText ?? string.Empty,
            LastDeckEditorGroupFilter = source.LastDeckEditorGroupFilter ?? "전체 그룹",
            LastDeckEditorCategoryFilter = source.LastDeckEditorCategoryFilter ?? "전체 등급",
            LastDeckEditorStatusFilter = source.LastDeckEditorStatusFilter ?? "전체 상태",
            LastDeckEditorSortMode = source.LastDeckEditorSortMode ?? "기본 정렬",
            LastDeckEditorFavoritesOnly = source.LastDeckEditorFavoritesOnly,
            LastDeckEditorBelovedOnly = source.LastDeckEditorBelovedOnly
        };
}
