using KotodamanWordFinder.Models;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder.Services;

public sealed class WordSearchService
{
    private readonly Dictionary<int, List<PreparedWord>> _wordsByLength;
    private readonly Dictionary<int, Dictionary<string, string>> _comboWordsByLength;

    public WordSearchService(IEnumerable<WordEntry> words)
    {
        _wordsByLength = new Dictionary<int, List<PreparedWord>>();
        _comboWordsByLength = new Dictionary<int, Dictionary<string, string>>();
        var seenKeysByLength = new Dictionary<int, HashSet<string>>();
        int shortComboWordCount = 0;

        // 21만+ 단어를 대형 GroupBy 배열로 두 번 재구성하지 않고 한 번의 순회로
        // 검색 목록과 콤보 사전을 동시에 만듭니다.
        foreach (WordEntry entry in words)
        {
            PreparedWord word = PrepareWord(entry);
            int length = word.Cells.Count;
            if (length is < 2 or > 7)
            {
                continue;
            }

            if (!seenKeysByLength.TryGetValue(length, out HashSet<string>? seenKeys) || seenKeys is null)
            {
                seenKeys = new HashSet<string>(StringComparer.Ordinal);
                seenKeysByLength[length] = seenKeys;
            }

            if (!seenKeys.Add(word.CellKey))
            {
                continue;
            }

            if (!_comboWordsByLength.TryGetValue(length, out Dictionary<string, string>? comboWords) || comboWords is null)
            {
                comboWords = new Dictionary<string, string>(StringComparer.Ordinal);
                _comboWordsByLength[length] = comboWords;
            }
            comboWords[word.CellKey] = word.Text;

            if (length is >= 4 and <= 7)
            {
                if (!_wordsByLength.TryGetValue(length, out List<PreparedWord>? searchWords) || searchWords is null)
                {
                    searchWords = new List<PreparedWord>();
                    _wordsByLength[length] = searchWords;
                }
                searchWords.Add(word);
            }
            else
            {
                shortComboWordCount++;
            }
        }

        foreach (List<PreparedWord> list in _wordsByLength.Values)
        {
            list.Sort((left, right) => StringComparer.Ordinal.Compare(left.Text, right.Text));
        }

        ShortComboWordCount = shortComboWordCount;
    }

    public int ShortComboWordCount { get; }
    public bool HasShortComboWords => ShortComboWordCount > 0;

    // 이미 찾아낸 결과에 필요한 문자들을 특정 캐릭터 묶음으로 실제 배정할 수 있는지 확인합니다.
    // 첫 턴 7장 성립률 계산에서 동일한 캐릭터 배정 규칙을 재사용합니다.
    public bool CanAssignResult(
        SearchResult result,
        IReadOnlyList<CharacterEntry> availableCharacters)
    {
        RequiredSlot[] requiredSlots = result.Assignments
            .Select(assignment => new RequiredSlot(
                assignment.BoardIndex,
                KanaUtility.NormalizeCell(assignment.Letter)))
            .ToArray();

        if (requiredSlots.Length == 0)
        {
            return true;
        }

        CharacterLetterSet[] characterLetters = availableCharacters
            .Select(character => new CharacterLetterSet(
                character,
                character.GetAvailableLetters()))
            .ToArray();

        return TryAssignCharacters(requiredSlots, characterLetters, out _);
    }

    public SearchGroup FindLongestWords(
        IReadOnlyList<string?> board,
        IReadOnlyList<CharacterEntry> availableCharacters,
        int minimumLength = 4,
        int maximumLength = 7,
        int maximumPlacements = 4,
        int maximumResults = 30)
    {
        IReadOnlyDictionary<int, SearchGroup> groups = FindWordsByLength(
            board,
            availableCharacters,
            minimumLength,
            maximumLength,
            maximumPlacements,
            maximumResults);

        for (int length = Math.Min(Math.Min(maximumLength, board.Count), 7);
             length >= minimumLength;
             length--)
        {
            if (groups.TryGetValue(length, out SearchGroup? group) && group.HasResults)
            {
                return group;
            }
        }

        return new SearchGroup();
    }

    public IReadOnlyDictionary<int, SearchGroup> FindWordsByLength(
        IReadOnlyList<string?> board,
        IReadOnlyList<CharacterEntry> availableCharacters,
        int minimumLength = 4,
        int maximumLength = 7,
        int maximumPlacements = 4,
        int maximumResultsPerLength = 30)
    {
        var groups = new Dictionary<int, SearchGroup>();
        if (board.Count == 0)
        {
            return groups;
        }

        string?[] normalizedBoard = board
            .Select(cell => string.IsNullOrWhiteSpace(cell)
                ? null
                : KanaUtility.NormalizeCell(cell))
            .ToArray();

        CharacterLetterSet[] characterLetters = availableCharacters
            .Select(character => new CharacterLetterSet(
                character,
                character.GetAvailableLetters()))
            .ToArray();

        int startLength = Math.Min(Math.Min(maximumLength, board.Count), 7);
        for (int length = startLength; length >= minimumLength; length--)
        {
            IReadOnlyList<SearchResult> results = FindWordsOfLength(
                normalizedBoard,
                characterLetters,
                length,
                maximumPlacements,
                maximumResultsPerLength);

            groups[length] = new SearchGroup
            {
                WordLength = length,
                Results = results
            };
        }

        return groups;
    }

    public IReadOnlyDictionary<int, SearchGroup> FindGeneralWordsByLength(
        IReadOnlyList<string?> board,
        int minimumLength = 4,
        int maximumLength = 7,
        int maximumPlacements = 4,
        int maximumResultsPerLength = 30)
    {
        var groups = new Dictionary<int, SearchGroup>();
        if (board.Count == 0)
        {
            return groups;
        }

        string?[] normalizedBoard = board
            .Select(cell => string.IsNullOrWhiteSpace(cell)
                ? null
                : KanaUtility.NormalizeCell(cell))
            .ToArray();

        int startLength = Math.Min(Math.Min(maximumLength, board.Count), 7);
        for (int length = startLength; length >= minimumLength; length--)
        {
            IReadOnlyList<SearchResult> results = FindGeneralWordsOfLength(
                normalizedBoard,
                length,
                maximumPlacements,
                maximumResultsPerLength);

            groups[length] = new SearchGroup
            {
                WordLength = length,
                Results = results
            };
        }

        return groups;
    }

    private IReadOnlyList<SearchResult> FindGeneralWordsOfLength(
        string?[] normalizedBoard,
        int wordLength,
        int maximumPlacements,
        int maximumResults)
    {
        if (!_wordsByLength.TryGetValue(wordLength, out List<PreparedWord>? words))
        {
            return Array.Empty<SearchResult>();
        }

        var found = new List<SearchResult>();
        var seenWords = new HashSet<string>(StringComparer.Ordinal);

        foreach (PreparedWord word in words)
        {
            for (int start = 0; start <= normalizedBoard.Length - wordLength; start++)
            {
                if (!TryMatchBoard(
                        word,
                        normalizedBoard,
                        start,
                        maximumPlacements,
                        out List<RequiredSlot> requiredSlots))
                {
                    continue;
                }

                if (requiredSlots.Count == 0)
                {
                    continue;
                }

                if (seenWords.Add(word.Text))
                {
                    CharacterAssignment[] assignments = requiredSlots
                        .Select(slot => new CharacterAssignment
                        {
                            BoardIndex = slot.BoardIndex,
                            Letter = slot.Letter,
                            CharacterId = $"general-{slot.BoardIndex}",
                            CharacterName = "필요 문자",
                            IsGeneralSuggestion = true
                        })
                        .OrderBy(assignment => assignment.BoardIndex)
                        .ToArray();

                    string?[] completedBoard = (string?[])normalizedBoard.Clone();
                    for (int offset = 0; offset < word.Cells.Count; offset++)
                    {
                        completedBoard[start + offset] = word.Cells[offset];
                    }

                    found.Add(new SearchResult
                    {
                        Word = word.Text,
                        Cells = word.Cells,
                        StartIndex = start,
                        EndIndex = start + wordLength - 1,
                        Assignments = assignments,
                        CompletedBoard = completedBoard.ToArray(),
                        ComboMatches = FindComboMatches(
                            completedBoard,
                            assignments.Select(assignment => assignment.BoardIndex).ToHashSet())
                    });
                }

                break;
            }
        }

        return found
            .OrderByDescending(result => result.ComboCount)
            .ThenBy(result => result.Assignments.Count)
            .ThenBy(result => result.StartIndex)
            .ThenBy(result => result.Word, StringComparer.Ordinal)
            .Take(maximumResults)
            .ToArray();
    }

    private IReadOnlyList<SearchResult> FindWordsOfLength(
        string?[] normalizedBoard,
        CharacterLetterSet[] characterLetters,
        int wordLength,
        int maximumPlacements,
        int maximumResults)
    {
        if (!_wordsByLength.TryGetValue(wordLength, out List<PreparedWord>? words))
        {
            return Array.Empty<SearchResult>();
        }

        var found = new List<SearchResult>();
        var seenWords = new HashSet<string>(StringComparer.Ordinal);

        foreach (PreparedWord word in words)
        {
            for (int start = 0; start <= normalizedBoard.Length - wordLength; start++)
            {
                if (!TryMatchBoard(
                        word,
                        normalizedBoard,
                        start,
                        maximumPlacements,
                        out List<RequiredSlot> requiredSlots))
                {
                    continue;
                }

                // 캐릭터를 한 명도 놓지 않는 기존 단어는 추천에서 제외합니다.
                if (requiredSlots.Count == 0)
                {
                    continue;
                }

                if (!TryAssignCharacters(requiredSlots, characterLetters, out IReadOnlyList<CharacterAssignment> assignments))
                {
                    continue;
                }

                if (seenWords.Add(word.Text))
                {
                    string?[] completedBoard = (string?[])normalizedBoard.Clone();
                    for (int offset = 0; offset < word.Cells.Count; offset++)
                    {
                        completedBoard[start + offset] = word.Cells[offset];
                    }

                    found.Add(new SearchResult
                    {
                        Word = word.Text,
                        Cells = word.Cells,
                        StartIndex = start,
                        EndIndex = start + wordLength - 1,
                        Assignments = assignments,
                        CompletedBoard = completedBoard.ToArray(),
                        ComboMatches = FindComboMatches(
                            completedBoard,
                            assignments.Select(assignment => assignment.BoardIndex).ToHashSet())
                    });
                }

                // 동일 단어의 여러 배치는 표시하지 않습니다.
                break;
            }
        }

        return found
            .OrderByDescending(result => result.ComboCount)
            .ThenBy(result => result.Assignments.Count)
            .ThenBy(result => result.StartIndex)
            .ThenBy(result => result.Word, StringComparer.Ordinal)
            .Take(maximumResults)
            .ToArray();
    }

    private IReadOnlyList<ComboMatch> FindComboMatches(
        IReadOnlyList<string?> board,
        IReadOnlySet<int> placedBoardIndexes)
    {
        var matches = new List<ComboMatch>();

        for (int start = 0; start < board.Count; start++)
        {
            if (board[start] is null)
            {
                continue;
            }

            var cells = new List<string>(7);
            bool containsPlacedCharacter = false;

            for (int end = start; end < board.Count && end - start < 7; end++)
            {
                string? cell = board[end];
                if (cell is null)
                {
                    break;
                }

                cells.Add(cell);
                if (placedBoardIndexes.Contains(end))
                {
                    containsPlacedCharacter = true;
                }

                int length = cells.Count;
                if (length < 2 || !containsPlacedCharacter)
                {
                    continue;
                }

                if (!_comboWordsByLength.TryGetValue(length, out Dictionary<string, string>? words))
                {
                    continue;
                }

                string key = string.Concat(cells);
                if (!words.TryGetValue(key, out string? displayWord))
                {
                    continue;
                }

                matches.Add(new ComboMatch
                {
                    Word = displayWord,
                    StartIndex = start,
                    EndIndex = end,
                    WordLength = length
                });
            }
        }

        return matches
            .OrderBy(match => match.StartIndex)
            .ThenBy(match => match.EndIndex)
            .ToArray();
    }

    private static bool TryMatchBoard(
        PreparedWord word,
        IReadOnlyList<string?> board,
        int startIndex,
        int maximumPlacements,
        out List<RequiredSlot> requiredSlots)
    {
        requiredSlots = new List<RequiredSlot>();

        for (int offset = 0; offset < word.Cells.Count; offset++)
        {
            int boardIndex = startIndex + offset;
            string targetLetter = word.Cells[offset];
            string? boardLetter = board[boardIndex];

            if (boardLetter is null)
            {
                requiredSlots.Add(new RequiredSlot(boardIndex, targetLetter));
                if (requiredSlots.Count > maximumPlacements)
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(boardLetter, targetLetter, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryAssignCharacters(
        IReadOnlyList<RequiredSlot> requiredSlots,
        IReadOnlyList<CharacterLetterSet> characters,
        out IReadOnlyList<CharacterAssignment> assignments)
    {
        assignments = Array.Empty<CharacterAssignment>();

        if (requiredSlots.Count > characters.Count)
        {
            return false;
        }

        var candidateCharacterIndexes = new List<int>[requiredSlots.Count];

        for (int slotIndex = 0; slotIndex < requiredSlots.Count; slotIndex++)
        {
            string neededLetter = requiredSlots[slotIndex].Letter;
            candidateCharacterIndexes[slotIndex] = new List<int>();

            for (int characterIndex = 0; characterIndex < characters.Count; characterIndex++)
            {
                if (characters[characterIndex].Letters.Contains(neededLetter))
                {
                    candidateCharacterIndexes[slotIndex].Add(characterIndex);
                }
            }

            // 같은 문자를 자체적으로 가진 캐릭터가 있다면 미라클 부여 문자에 의존하지 않는 쪽을 우선합니다.
            candidateCharacterIndexes[slotIndex] = candidateCharacterIndexes[slotIndex]
                .OrderBy(characterIndex =>
                    characters[characterIndex].Character.UsesMiracleGrantedLetter(neededLetter) ||
                    characters[characterIndex].Character.UsesDeckGroupGrantedLetter(neededLetter))
                .ThenBy(characterIndex => characterIndex)
                .ToList();

            if (candidateCharacterIndexes[slotIndex].Count == 0)
            {
                return false;
            }
        }

        // 선택지가 적은 칸부터 배정하면 불가능한 조합을 빠르게 걸러낼 수 있습니다.
        int[] slotOrder = Enumerable.Range(0, requiredSlots.Count)
            .OrderBy(index => candidateCharacterIndexes[index].Count)
            .ToArray();

        var usedCharacters = new bool[characters.Count];
        int[] selectedCharacterBySlot = Enumerable.Repeat(-1, requiredSlots.Count).ToArray();

        bool Search(int orderIndex)
        {
            if (orderIndex >= slotOrder.Length)
            {
                return true;
            }

            int slotIndex = slotOrder[orderIndex];

            foreach (int characterIndex in candidateCharacterIndexes[slotIndex])
            {
                if (usedCharacters[characterIndex])
                {
                    continue;
                }

                usedCharacters[characterIndex] = true;
                selectedCharacterBySlot[slotIndex] = characterIndex;

                if (Search(orderIndex + 1))
                {
                    return true;
                }

                selectedCharacterBySlot[slotIndex] = -1;
                usedCharacters[characterIndex] = false;
            }

            return false;
        }

        if (!Search(0))
        {
            return false;
        }

        assignments = requiredSlots
            .Select((slot, slotIndex) =>
            {
                CharacterEntry character = characters[selectedCharacterBySlot[slotIndex]].Character;
                CharacterForm? selectedForm = character.FindForm(character.ActiveFormId)
                    ?? character.ResolveFormForLetter(slot.Letter);
                return new CharacterAssignment
                {
                    BoardIndex = slot.BoardIndex,
                    Letter = slot.Letter,
                    CharacterId = character.Id,
                    CharacterName = character.Name,
                    CharacterFormId = selectedForm?.Id ?? CharacterEntry.BaseFormId,
                    CharacterFormName = selectedForm?.Name ?? "기본 형태",
                    UsesAlternateForm = selectedForm is not null,
                    LetterStateId = character.ActiveLetterStateId ?? CharacterEntry.BaseLetterStateId,
                    LetterStateName = character.GetActiveLetterStateName(),
                    LetterStateKind = character.GetActiveLetterStateKind(),
                    LetterStateNote = character.FindLetterState(character.ActiveLetterStateId)?.Note ?? string.Empty,
                    UsesSpecialLetterState = character.UsesSpecialLetterState,
                    CharacterGroupName = character.GroupName ?? string.Empty,
                    UsesMiracleLeaderLetter = character.UsesMiracleGrantedLetter(slot.Letter),
                    MiracleLeaderName = character.ActiveMiracleLeaderName ?? string.Empty,
                    MiracleEffectNote = character.ActiveMiracleEffectNote ?? string.Empty,
                    UsesDeckGroupConditionLetter = character.UsesDeckGroupGrantedLetter(slot.Letter),
                    DeckGroupConditionText = character.ActiveDeckGroupConditionText ?? string.Empty,
                    DeckGroupEffectNote = character.ActiveDeckGroupEffectNote ?? string.Empty
                };
            })
            .OrderBy(assignment => assignment.BoardIndex)
            .ToArray();

        return true;
    }

    private static PreparedWord PrepareWord(WordEntry entry)
    {
        // JsonDataLoader에서 이미 FormC 정규화와 칸 분해를 끝냈으므로
        // 일반 시작 경로에서는 21만+ 단어를 여기서 다시 Normalize하지 않습니다.
        IReadOnlyList<string> cells = entry.Cells is { Count: > 0 }
            ? entry.Cells
            : KanaUtility.SplitIntoCells(entry.Text);
        string text = entry.Text.IsNormalized(System.Text.NormalizationForm.FormC)
            ? entry.Text
            : entry.Text.Normalize(System.Text.NormalizationForm.FormC);

        return new PreparedWord(
            text,
            cells,
            string.Concat(cells));
    }

    private sealed record PreparedWord(
        string Text,
        IReadOnlyList<string> Cells,
        string CellKey);

    private sealed record RequiredSlot(int BoardIndex, string Letter);
    private sealed record CharacterLetterSet(CharacterEntry Character, HashSet<string> Letters);
}
