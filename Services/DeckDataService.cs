using System.IO;
using System.Text;
using System.Text.Json;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder.Services;

public static class DeckDataService
{
    private static readonly IReadOnlyDictionary<string, string[]> BuiltInGroupInclusions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["全の戦律"] = new[]
            {
                "斬の戦律", "砲の戦律", "突の戦律",
                "重の戦律", "超の戦律", "打の戦律"
            },
            ["三国の願い"] = new[]
            {
                "セイユニマ", "ブリタンディ", "リザンテクス"
            },
            ["『夢』への旅路"] = new[]
            {
                "セイユニマ", "ブリタンディ", "リザンテクス",
                "廻る魂", "此方へ", "月の夢"
            }
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 2천 명 이상 캐릭터 데이터는 들여쓰기 공백만으로도 1MB 이상 커집니다.
        // 읽기는 스트림 기반이므로 압축 JSON으로 저장해 디스크 쓰기와 백업 용량을 줄입니다.
        WriteIndented = false
    };

    public static void Save(string path, IEnumerable<CharacterEntry> characters)
    {
        CharacterEntry[] sourceCharacters = characters.ToArray();
        SynchronizeSharedGroupInclusions(sourceCharacters);

        var normalized = sourceCharacters
            .Where(character => !string.IsNullOrWhiteSpace(character.Name))
            .Select((character, index) => new CharacterEntry
            {
                Id = string.IsNullOrWhiteSpace(character.Id)
                    ? $"deck-slot-{index + 1:00}"
                    : character.Id.Trim(),
                Name = character.Name.Trim(),
                SearchAliases = NormalizeSearchAliases(character.SearchAliases),
                Category = CharacterCategories.Normalize(character.Category),
                Attribute = NormalizeAttribute(character.Attribute),
                SubAttributes = NormalizeAttributes(character.SubAttributes, character.Attribute),
                Species = NormalizeSpecies(character.Species),
                GroupName = NormalizeGroupName(character.GroupName),
                IncludedGroups = NormalizeGroupNames(character.IncludedGroups),
                IsFavorite = character.IsFavorite,
                IsBeloved = character.IsBeloved,
                ImageFileName = Path.GetFileName(character.ImageFileName ?? string.Empty),
                Letters = NormalizeLetters(character.Letters),
                AlternateForms = NormalizeCharacterForms(character.AlternateForms),
                LetterStates = NormalizeLetterStates(character.LetterStates),
                DeckRestrictionGroupId = (character.DeckRestrictionGroupId ?? string.Empty).Trim(),
                MiracleLeaderEffect = NormalizeMiracleLeaderEffect(character.MiracleLeaderEffect),
                DeckGroupLetterEffect = NormalizeDeckGroupLetterEffect(character.DeckGroupLetterEffect),
                OneTimeLetters = NormalizeLetters(character.OneTimeLetters),
                UsedOneTimeLetters = NormalizeLetters(character.UsedOneTimeLetters)
            })
            .Where(character =>
                character.Letters.Count > 0 ||
                character.OneTimeLetters.Count > 0 ||
                character.AlternateForms.Any(form => form.Letters.Count > 0) ||
                character.LetterStates.Any(state => state.Letters.Count > 0))
            .ToList();

        if (normalized.Count == 0)
        {
            throw new InvalidOperationException("문자가 등록된 캐릭터가 한 명도 없습니다.");
        }

        var duplicateId = normalized
            .GroupBy(character => character.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidOperationException($"캐릭터 ID가 중복되었습니다: {duplicateId.Key}");
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = path + ".tmp";
        string backupPath = path + ".backup";

        // 큰 characters.json을 문자열 하나로 통째로 만든 뒤 다시 파일에 쓰지 않고
        // JSON 직렬화 결과를 곧바로 임시 파일 스트림에 기록해 피크 메모리를 줄입니다.
        using (FileStream stream = new FileStream(
                   temporaryPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 64 * 1024,
                   FileOptions.SequentialScan))
        {
            JsonSerializer.Serialize(stream, normalized, JsonOptions);
        }

        if (File.Exists(path))
        {
            File.Copy(path, backupPath, overwrite: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public static MiracleLeaderEffect NormalizeMiracleLeaderEffect(MiracleLeaderEffect? effect)
    {
        if (effect is null)
        {
            return new MiracleLeaderEffect();
        }

        List<string> targetGroups = NormalizeGroupNames(effect.TargetGroups);
        List<string> grantedLetters = NormalizeLetters(effect.GrantedLetters);
        bool hasCompleteRule = targetGroups.Count > 0 && grantedLetters.Count > 0;

        return new MiracleLeaderEffect
        {
            // 대상 그룹과 부여 문자가 모두 입력되어 있으면 별도 체크 여부와 관계없이
            // 실제 미라클 리더 효과로 취급합니다. 발동 여부는 덱 1번 리더인지로 결정됩니다.
            IsEnabled = hasCompleteRule,
            TargetGroups = targetGroups,
            GrantedLetters = grantedLetters,
            Note = (effect.Note ?? string.Empty).Trim()
        };
    }

    public static DeckGroupLetterEffect NormalizeDeckGroupLetterEffect(DeckGroupLetterEffect? effect)
    {
        if (effect is null)
        {
            return new DeckGroupLetterEffect();
        }

        List<string> targetGroups = NormalizeGroupNames(effect.TargetGroups);
        List<string> grantedLetters = NormalizeLetters(effect.GrantedLetters);
        int minimumCount = Math.Max(1, effect.MinimumCount);
        bool hasCompleteRule = targetGroups.Count > 0 && grantedLetters.Count > 0;

        return new DeckGroupLetterEffect
        {
            IsEnabled = hasCompleteRule,
            TargetGroups = targetGroups,
            MinimumCount = minimumCount,
            GrantedLetters = grantedLetters,
            Note = (effect.Note ?? string.Empty).Trim()
        };
    }

    public static List<string> NormalizeSearchAliases(IEnumerable<string>? aliases)
    {
        if (aliases is null)
        {
            return new List<string>();
        }

        return aliases
            .Select(alias => (alias ?? string.Empty).Normalize(NormalizationForm.FormC).Trim())
            .Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeGroupName(string? groupName)
        => (groupName ?? string.Empty)
            .Normalize(NormalizationForm.FormC)
            .Trim();

    public static string NormalizeAttribute(string? attribute)
    {
        string normalized = (attribute ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (normalized.EndsWith("属性", StringComparison.Ordinal))
        {
            normalized = normalized[..^2].Trim();
        }

        return normalized is "火" or "水" or "木" or "光" or "闇" or "天" or "冥" or "虹"
            ? normalized
            : string.Empty;
    }

    public static List<string> NormalizeAttributes(IEnumerable<string>? attributes, string? mainAttribute = null)
    {
        string main = NormalizeAttribute(mainAttribute);
        if (attributes is null)
        {
            return new List<string>();
        }

        return attributes
            .Select(NormalizeAttribute)
            .Where(value => value.Length > 0 && !string.Equals(value, main, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string NormalizeSpecies(string? species)
    {
        string normalized = (species ?? string.Empty).Normalize(NormalizationForm.FormC).Trim();
        if (normalized.EndsWith("種族", StringComparison.Ordinal))
        {
            normalized = normalized[..^2].Trim();
        }

        return normalized is "神" or "魔" or "英" or "龍" or "獣" or "霊" or "物" or "妖"
            ? normalized
            : string.Empty;
    }

    public static List<string> NormalizeGroupNames(IEnumerable<string>? groupNames)
    {
        if (groupNames is null)
        {
            return new List<string>();
        }

        return groupNames
            .Select(NormalizeGroupName)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsLegacyGroupInclusionData(MiracleLeaderEffect? effect)
    {
        MiracleLeaderEffect normalized = NormalizeMiracleLeaderEffect(effect);
        return normalized.TargetGroups.Count > 0 &&
               normalized.GrantedLetters.Count == 0 &&
               string.IsNullOrWhiteSpace(normalized.Note);
    }

    public static List<string> GetEffectiveGroupNames(CharacterEntry character)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>();

        void AddGroup(string? value)
        {
            string normalized = NormalizeGroupName(value);
            if (normalized.Length > 0 && result.Add(normalized))
            {
                pending.Enqueue(normalized);
            }
        }

        AddGroup(character.GroupName);
        foreach (string group in NormalizeGroupNames(character.IncludedGroups))
        {
            AddGroup(group);
        }

        // 자주 쓰이는 상위 그룹은 기존 데이터가 아직 저장되지 않았더라도 즉시 동작합니다.
        while (pending.Count > 0)
        {
            string group = pending.Dequeue();
            if (!BuiltInGroupInclusions.TryGetValue(group, out string[]? included) || included is null)
            {
                continue;
            }

            foreach (string childGroup in included)
            {
                AddGroup(childGroup);
            }
        }

        return result.OrderBy(group => group, StringComparer.Ordinal).ToList();
    }

    public static bool CharacterMatchesTargetGroups(
        CharacterEntry character,
        IEnumerable<string>? targetGroups)
    {
        HashSet<string> targets = NormalizeGroupNames(targetGroups)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return targets.Count > 0 &&
               GetEffectiveGroupNames(character).Any(targets.Contains);
    }

    public static void SynchronizeSharedGroupInclusions(IEnumerable<CharacterEntry> characters)
    {
        CharacterEntry[] entries = characters.ToArray();
        var rules = entries
            .Where(character => NormalizeGroupName(character.GroupName).Length > 0)
            .GroupBy(character => NormalizeGroupName(character.GroupName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .SelectMany(character => NormalizeGroupNames(character.IncludedGroups))
                    .Concat(BuiltInGroupInclusions.TryGetValue(group.Key, out string[]? builtIn) && builtIn is not null
                        ? builtIn
                        : Array.Empty<string>())
                    .Select(NormalizeGroupName)
                    .Where(included => included.Length > 0 &&
                                       !string.Equals(included, group.Key, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(included => included, StringComparer.Ordinal)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (CharacterEntry character in entries)
        {
            string groupName = NormalizeGroupName(character.GroupName);
            character.IncludedGroups = groupName.Length > 0 && rules.TryGetValue(groupName, out List<string>? included)
                ? included.ToList()
                : new List<string>();
        }
    }


    public static List<CharacterForm> NormalizeCharacterForms(
        IEnumerable<CharacterForm>? forms)
    {
        if (forms is null)
        {
            return new List<CharacterForm>();
        }

        var result = new List<CharacterForm>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CharacterForm source in forms)
        {
            string name = (source.Name ?? string.Empty).Trim();
            List<string> letters = NormalizeLetters(source.Letters);
            if (name.Length == 0 || letters.Count == 0 || !usedNames.Add(name))
            {
                continue;
            }

            string id = string.IsNullOrWhiteSpace(source.Id)
                ? $"form-{Guid.NewGuid():N}"
                : source.Id.Trim();
            if (!usedIds.Add(id))
            {
                id = $"form-{Guid.NewGuid():N}";
                usedIds.Add(id);
            }

            result.Add(new CharacterForm
            {
                Id = id,
                Name = name,
                ImageFileName = Path.GetFileName(source.ImageFileName ?? string.Empty),
                Letters = letters,
                Attribute = NormalizeAttribute(source.Attribute),
                SubAttributes = NormalizeAttributes(source.SubAttributes, source.Attribute),
                Species = NormalizeSpecies(source.Species),
                Note = (source.Note ?? string.Empty).Trim()
            });
        }

        return result;
    }

    public static List<CharacterLetterState> NormalizeLetterStates(
        IEnumerable<CharacterLetterState>? states)
    {
        if (states is null)
        {
            return new List<CharacterLetterState>();
        }

        var result = new List<CharacterLetterState>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CharacterLetterState source in states)
        {
            string name = (source.Name ?? string.Empty).Trim();
            List<string> letters = NormalizeLetters(source.Letters);
            if (name.Length == 0 || letters.Count == 0 || !usedNames.Add(name))
            {
                continue;
            }

            string id = string.IsNullOrWhiteSpace(source.Id)
                ? $"state-{Guid.NewGuid():N}"
                : source.Id.Trim();
            if (!usedIds.Add(id))
            {
                id = $"state-{Guid.NewGuid():N}";
                usedIds.Add(id);
            }

            result.Add(new CharacterLetterState
            {
                Id = id,
                Name = name,
                Kind = CharacterLetterStateKinds.Normalize(source.Kind),
                IncludeBaseLetters = source.IncludeBaseLetters,
                Letters = letters,
                Note = (source.Note ?? string.Empty).Trim()
            });
        }

        return result;
    }

    public static List<string> NormalizeLetters(IEnumerable<string>? letters)
    {
        if (letters is null)
        {
            return new List<string>();
        }

        return letters
            .Where(letter => !string.IsNullOrWhiteSpace(letter))
            .Select(KanaUtility.NormalizeCell)
            .Where(letter => letter.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
