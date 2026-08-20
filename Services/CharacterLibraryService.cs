using System.IO;
using KotodamanWordFinder.Models;

namespace KotodamanWordFinder.Services;

public static class CharacterLibraryService
{
    public static IReadOnlyList<CharacterEntry> LoadOrCreate(
        string libraryPath,
        IReadOnlyList<CharacterEntry> currentDeck)
    {
        var merged = new Dictionary<string, CharacterEntry>(StringComparer.Ordinal);

        if (File.Exists(libraryPath))
        {
            foreach (CharacterEntry character in JsonDataLoader.LoadCharacters(libraryPath))
            {
                if (string.IsNullOrWhiteSpace(character.Id))
                {
                    continue;
                }

                merged[character.Id] = Clone(character);
            }
        }

        // v1.1 이하에서는 deck.json 자체가 캐릭터 목록 역할도 했습니다.
        // characters.json이 없거나 일부 캐릭터가 빠져 있으면 현재 덱을 자동으로 합칩니다.
        foreach (CharacterEntry character in currentDeck)
        {
            if (string.IsNullOrWhiteSpace(character.Id))
            {
                continue;
            }

            if (!merged.ContainsKey(character.Id))
            {
                merged[character.Id] = Clone(character);
            }
        }

        CharacterEntry[] result = merged.Values
            .OrderBy(character => character.Name, StringComparer.Ordinal)
            .ThenBy(character => character.Id, StringComparer.Ordinal)
            .ToArray();
        DeckDataService.SynchronizeSharedGroupInclusions(result);
        return result;
    }

    public static CharacterEntry Clone(CharacterEntry character)
        => new()
        {
            Id = character.Id,
            Name = character.Name,
            SearchAliases = DeckDataService.NormalizeSearchAliases(character.SearchAliases),
            Category = CharacterCategories.Normalize(character.Category),
            Attribute = DeckDataService.NormalizeAttribute(character.Attribute),
            SubAttributes = DeckDataService.NormalizeAttributes(character.SubAttributes, character.Attribute),
            Species = DeckDataService.NormalizeSpecies(character.Species),
            GroupName = DeckDataService.NormalizeGroupName(character.GroupName),
            IncludedGroups = DeckDataService.NormalizeGroupNames(character.IncludedGroups),
            IsFavorite = character.IsFavorite,
            IsBeloved = character.IsBeloved,
            ImageFileName = Path.GetFileName(character.ImageFileName ?? string.Empty),
            Letters = (character.Letters ?? new List<string>()).ToList(),
            AlternateForms = (character.AlternateForms ?? new List<CharacterForm>())
                .Select(CloneForm)
                .ToList(),
            LetterStates = (character.LetterStates ?? new List<CharacterLetterState>())
                .Select(CloneState)
                .ToList(),
            DeckRestrictionGroupId = character.DeckRestrictionGroupId ?? string.Empty,
            MiracleLeaderEffect = CloneMiracleLeaderEffect(character.MiracleLeaderEffect),
            DeckGroupLetterEffect = CloneDeckGroupLetterEffect(character.DeckGroupLetterEffect),
            OneTimeLetters = (character.OneTimeLetters ?? new List<string>()).ToList(),
            UsedOneTimeLetters = (character.UsedOneTimeLetters ?? new List<string>()).ToList(),
            ActiveLetterStateId = character.ActiveLetterStateId,
            ActiveFormId = character.ActiveFormId,
            ActiveMiracleGrantedLetters = (character.ActiveMiracleGrantedLetters ?? new List<string>()).ToList(),
            ActiveMiracleLeaderName = character.ActiveMiracleLeaderName ?? string.Empty,
            ActiveMiracleEffectNote = character.ActiveMiracleEffectNote ?? string.Empty,
            ActiveDeckGroupGrantedLetters = (character.ActiveDeckGroupGrantedLetters ?? new List<string>()).ToList(),
            ActiveDeckGroupConditionText = character.ActiveDeckGroupConditionText ?? string.Empty,
            ActiveDeckGroupEffectNote = character.ActiveDeckGroupEffectNote ?? string.Empty
        };

    public static MiracleLeaderEffect CloneMiracleLeaderEffect(MiracleLeaderEffect? effect)
    {
        MiracleLeaderEffect normalized = DeckDataService.NormalizeMiracleLeaderEffect(effect);
        return new MiracleLeaderEffect
        {
            IsEnabled = normalized.IsEnabled,
            TargetGroups = normalized.TargetGroups.ToList(),
            GrantedLetters = normalized.GrantedLetters.ToList(),
            Note = normalized.Note
        };
    }

    public static DeckGroupLetterEffect CloneDeckGroupLetterEffect(DeckGroupLetterEffect? effect)
    {
        DeckGroupLetterEffect normalized = DeckDataService.NormalizeDeckGroupLetterEffect(effect);
        return new DeckGroupLetterEffect
        {
            IsEnabled = normalized.IsEnabled,
            TargetGroups = normalized.TargetGroups.ToList(),
            MinimumCount = normalized.MinimumCount,
            GrantedLetters = normalized.GrantedLetters.ToList(),
            Note = normalized.Note
        };
    }

    public static CharacterLetterState CloneState(CharacterLetterState state)
        => new()
        {
            Id = state.Id,
            Name = state.Name,
            Kind = CharacterLetterStateKinds.Normalize(state.Kind),
            IncludeBaseLetters = state.IncludeBaseLetters,
            Letters = (state.Letters ?? new List<string>()).ToList(),
            Note = state.Note ?? string.Empty
        };

    public static CharacterForm CloneForm(CharacterForm form)
        => new()
        {
            Id = form.Id,
            Name = form.Name,
            ImageFileName = Path.GetFileName(form.ImageFileName ?? string.Empty),
            Letters = (form.Letters ?? new List<string>()).ToList(),
            Attribute = DeckDataService.NormalizeAttribute(form.Attribute),
            SubAttributes = DeckDataService.NormalizeAttributes(form.SubAttributes, form.Attribute),
            Species = DeckDataService.NormalizeSpecies(form.Species),
            Note = form.Note ?? string.Empty
        };
}
