using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder.Services;

public static class JsonDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<WordEntry> LoadWords(
        string jsonPath,
        string? textPath = null,
        params string[] additionalJsonPaths)
    {
        // 대형 사전에서 GroupBy/OrderBy 중간 배열을 여러 번 만들지 않고
        // 단어를 읽는 즉시 정규화 + 칸 분해 + 우선순위 중복 제거를 한 번에 수행합니다.
        var selected = new Dictionary<string, WordEntry>(StringComparer.Ordinal);

        void AddWords(IEnumerable<WordEntry> source)
        {
            foreach (WordEntry rawWord in source)
            {
                if (string.IsNullOrWhiteSpace(rawWord.Text))
                {
                    continue;
                }

                WordEntry word = NormalizeWord(rawWord);
                int cellCount = word.Cells?.Count ?? 0;
                if (cellCount is < 2 or > 7)
                {
                    continue;
                }

                if (!selected.TryGetValue(word.Text, out WordEntry? existing) ||
                    existing is null ||
                    IsPreferredWord(word, existing))
                {
                    selected[word.Text] = word;
                }
            }
        }

        AddWords(LoadWordJson(jsonPath, defaultSource: "BuiltIn", required: true));

        foreach (string additionalPath in additionalJsonPaths)
        {
            if (!string.IsNullOrWhiteSpace(additionalPath))
            {
                AddWords(LoadWordJson(additionalPath, defaultSource: "GACCAG", required: false));
            }
        }

        if (!string.IsNullOrWhiteSpace(textPath) && File.Exists(textPath))
        {
            AddWords(LoadWordsFromText(textPath));
        }

        return selected.Values.ToArray();
    }

    private static bool IsPreferredWord(WordEntry candidate, WordEntry existing)
    {
        int candidatePriority = GetSourcePriority(candidate);
        int existingPriority = GetSourcePriority(existing);
        if (candidatePriority != existingPriority)
        {
            return candidatePriority > existingPriority;
        }

        bool candidateHasDescription = !string.IsNullOrWhiteSpace(candidate.Description);
        bool existingHasDescription = !string.IsNullOrWhiteSpace(existing.Description);
        return candidateHasDescription && !existingHasDescription;
    }

    public static IReadOnlyList<CharacterEntry> LoadCharacters(string path)
    {
        List<CharacterEntry> result = LoadList<CharacterEntry>(path, required: true)
            .Select(character =>
            {
                MiracleLeaderEffect miracle = DeckDataService.NormalizeMiracleLeaderEffect(
                    character.MiracleLeaderEffect);
                List<string> includedGroups = DeckDataService.NormalizeGroupNames(character.IncludedGroups);

                // v1.13까지는 상위 그룹의 포함 대상을 미라클 대상 그룹 칸에 임시로
                // 입력한 데이터가 있었습니다. 부여 문자가 없는 규칙은 그룹 포함 규칙으로 자동 이전합니다.
                if (DeckDataService.IsLegacyGroupInclusionData(miracle))
                {
                    includedGroups = includedGroups
                        .Concat(miracle.TargetGroups)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    miracle = new MiracleLeaderEffect();
                }

                return new CharacterEntry
                {
                    Id = (character.Id ?? string.Empty).Trim(),
                    Name = (character.Name ?? string.Empty).Trim(),
                    SearchAliases = DeckDataService.NormalizeSearchAliases(character.SearchAliases),
                    Category = CharacterCategories.Normalize(character.Category),
                    Attribute = DeckDataService.NormalizeAttribute(character.Attribute),
                    SubAttributes = DeckDataService.NormalizeAttributes(character.SubAttributes, character.Attribute),
                    Species = DeckDataService.NormalizeSpecies(character.Species),
                    GroupName = DeckDataService.NormalizeGroupName(character.GroupName),
                    IncludedGroups = includedGroups,
                    IsFavorite = character.IsFavorite,
                    IsBeloved = character.IsBeloved,
                    ImageFileName = Path.GetFileName(character.ImageFileName ?? string.Empty),
                    Letters = DeckDataService.NormalizeLetters(character.Letters),
                    AlternateForms = DeckDataService.NormalizeCharacterForms(character.AlternateForms),
                    LetterStates = DeckDataService.NormalizeLetterStates(character.LetterStates),
                    DeckRestrictionGroupId = (character.DeckRestrictionGroupId ?? string.Empty).Trim(),
                    MiracleLeaderEffect = miracle,
                    DeckGroupLetterEffect = DeckDataService.NormalizeDeckGroupLetterEffect(character.DeckGroupLetterEffect),
                    OneTimeLetters = DeckDataService.NormalizeLetters(character.OneTimeLetters),
                    UsedOneTimeLetters = DeckDataService.NormalizeLetters(character.UsedOneTimeLetters)
                };
            })
            .Where(character => character.Id.Length > 0 && character.Name.Length > 0)
            .ToList();

        DeckDataService.SynchronizeSharedGroupInclusions(result);
        return result;
    }

    public static GaccagUpdateMetadata? LoadGaccagMetadata(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<GaccagUpdateMetadata>(json, JsonOptions);
        }
        catch
        {
            // 메타데이터가 손상되어도 실제 단어 파일은 정상적으로 사용할 수 있습니다.
            return null;
        }
    }

    private static IEnumerable<WordEntry> LoadWordJson(
        string path,
        string defaultSource,
        bool required)
    {
        return LoadList<WordEntry>(path, required)
            .Select(word =>
            {
                word.Source = string.IsNullOrWhiteSpace(word.Source)
                    ? defaultSource
                    : word.Source.Trim();
                return word;
            });
    }

    private static WordEntry NormalizeWord(WordEntry word)
    {
        string normalizedText = word.Text.Trim().Normalize(NormalizationForm.FormC);
        List<string> normalizedCells = word.Cells is { Count: > 0 }
            ? word.Cells
                .Where(cell => !string.IsNullOrWhiteSpace(cell))
                .Select(cell => cell.Trim().Normalize(NormalizationForm.FormC))
                .ToList()
            : KanaUtility.SplitIntoCells(normalizedText).ToList();

        return new WordEntry
        {
            Text = normalizedText,
            Cells = normalizedCells,
            Description = string.IsNullOrWhiteSpace(word.Description)
                ? null
                : word.Description.Trim(),
            Source = string.IsNullOrWhiteSpace(word.Source)
                ? null
                : word.Source.Trim()
        };
    }

    private static int GetSourcePriority(WordEntry word)
    {
        return word.Source?.ToUpperInvariant() switch
        {
            "USER" => 30,
            "BUILTIN" => 20,
            "GACCAG" => 10,
            _ => 0
        };
    }

    private static IEnumerable<WordEntry> LoadWordsFromText(string path)
    {
        foreach (string rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] columns = line.Split('\t', 2);
            string text = columns[0].Trim();
            if (text.Length == 0)
            {
                continue;
            }

            yield return new WordEntry
            {
                Text = text,
                Description = columns.Length > 1 && !string.IsNullOrWhiteSpace(columns[1])
                    ? columns[1].Trim()
                    : null,
                Source = "User"
            };
        }
    }

    private static IReadOnlyList<T> LoadList<T>(string path, bool required)
    {
        string actualPath = ResolveExistingJsonPath(path);
        if (!File.Exists(actualPath))
        {
            if (required)
            {
                throw new FileNotFoundException($"데이터 파일을 찾을 수 없습니다: {path}");
            }

            return Array.Empty<T>();
        }

        if (actualPath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using FileStream fileStream = File.OpenRead(actualPath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<List<T>>(gzipStream, JsonOptions)
                   ?? new List<T>();
        }

        using FileStream stream = File.OpenRead(actualPath);
        return JsonSerializer.Deserialize<List<T>>(stream, JsonOptions)
               ?? new List<T>();
    }
    private static string ResolveExistingJsonPath(string path)
    {
        if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            string gzipPath = path + ".gz";
            if (File.Exists(gzipPath))
            {
                return gzipPath;
            }
        }

        return path;
    }

}
