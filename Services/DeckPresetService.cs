using System.IO;
using System.Text;
using System.Text.Json;
using KotodamanWordFinder.Models;

namespace KotodamanWordFinder.Services;

public static class DeckPresetService
{
    private const int MaximumDeckSize = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<DeckPreset> LoadOrCreate(
        string path,
        IReadOnlyList<CharacterEntry> currentDeck)
    {
        var result = new List<DeckPreset>();

        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                List<DeckPreset>? loaded = JsonSerializer.Deserialize<List<DeckPreset>>(json, JsonOptions);
                if (loaded is not null)
                {
                    result.AddRange(loaded.Select(Clone));
                }
            }
            catch
            {
                // 손상된 프리셋 파일은 현재 덱으로 다시 시작합니다.
            }
        }

        Normalize(result);

        if (result.Count == 0 && currentDeck.Count > 0)
        {
            result.Add(new DeckPreset
            {
                Id = $"preset-{Guid.NewGuid():N}",
                Name = "기본 덱",
                CharacterIds = currentDeck
                    .Select(character => character.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .Take(MaximumDeckSize)
                    .ToList()
            });
        }

        return result
            .OrderBy(preset => preset.Name, StringComparer.Ordinal)
            .ThenBy(preset => preset.Id, StringComparer.Ordinal)
            .Select(Clone)
            .ToArray();
    }

    public static void Save(string path, IEnumerable<DeckPreset> presets)
    {
        List<DeckPreset> normalized = presets.Select(Clone).ToList();
        Normalize(normalized);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + ".tmp";
        string backupPath = path + ".backup";
        string json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));

        if (File.Exists(path))
        {
            File.Copy(path, backupPath, overwrite: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public static DeckPreset Clone(DeckPreset preset)
        => new()
        {
            Id = preset.Id,
            Name = preset.Name,
            CharacterIds = preset.CharacterIds.ToList()
        };

    private static void Normalize(List<DeckPreset> presets)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = presets.Count - 1; index >= 0; index--)
        {
            DeckPreset preset = presets[index];
            preset.Id = string.IsNullOrWhiteSpace(preset.Id)
                ? $"preset-{Guid.NewGuid():N}"
                : preset.Id.Trim();
            preset.Name = preset.Name.Trim();
            preset.CharacterIds = preset.CharacterIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(MaximumDeckSize)
                .ToList();

            if (preset.Name.Length == 0 ||
                !usedIds.Add(preset.Id) ||
                !usedNames.Add(preset.Name))
            {
                presets.RemoveAt(index);
            }
        }
    }
}
