using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Services;
using KotodamanWordFinder.Utilities;

namespace KotodamanWordFinder;

public partial class LetterStateEditorWindow : Window
{
    private const string AddModeText = "기본 문자에 추가";
    private const string ReplaceModeText = "기본 문자를 대체";

    private readonly List<CharacterLetterState> _states;
    private string? _editingStateId;
    private bool _isRefreshing;

    public LetterStateEditorWindow(
        string characterName,
        IEnumerable<CharacterLetterState> states)
    {
        InitializeComponent();

        TitleText.Text = string.IsNullOrWhiteSpace(characterName)
            ? "새 캐릭터 · 문자 상태 편집"
            : $"{characterName} · 문자 상태 편집";
        StateKindComboBox.ItemsSource = CharacterLetterStateKinds.All;
        StateKindComboBox.SelectedItem = CharacterLetterStateKinds.Conditional;
        StateMergeModeComboBox.ItemsSource = new[] { AddModeText, ReplaceModeText };
        StateMergeModeComboBox.SelectedItem = AddModeText;

        _states = DeckDataService.NormalizeLetterStates(states)
            .Select(CharacterLibraryService.CloneState)
            .ToList();

        RefreshStateList();
        if (_states.Count > 0)
        {
            BeginEditing(_states[0].Id);
        }
        else
        {
            ClearEditor();
        }
    }

    public IReadOnlyList<CharacterLetterState> SavedStates { get; private set; }
        = Array.Empty<CharacterLetterState>();

    private void RefreshStateList(string? selectedId = null)
    {
        _isRefreshing = true;
        try
        {
            StateDisplayItem[] items = _states
                .Select(state => new StateDisplayItem(state))
                .ToArray();
            StateListBox.ItemsSource = items;

            string? targetId = selectedId ?? _editingStateId;
            StateDisplayItem? selected = items.FirstOrDefault(item =>
                string.Equals(item.Id, targetId, StringComparison.Ordinal));
            StateListBox.SelectedItem = selected;
            if (selected is not null)
            {
                StateListBox.ScrollIntoView(selected);
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void StateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || StateListBox.SelectedItem is not StateDisplayItem item)
        {
            return;
        }

        BeginEditing(item.Id);
    }

    private void BeginEditing(string stateId)
    {
        CharacterLetterState? state = FindState(stateId);
        if (state is null)
        {
            return;
        }

        _editingStateId = state.Id;
        StateNameTextBox.Text = state.Name;
        StateKindComboBox.SelectedItem = CharacterLetterStateKinds.Normalize(state.Kind);
        StateLettersTextBox.Text = string.Join(" ", state.Letters);
        StateMergeModeComboBox.SelectedItem = state.IncludeBaseLetters
            ? AddModeText
            : ReplaceModeText;
        StateNoteTextBox.Text = state.Note;
        UpdateStateButton.IsEnabled = true;
        StateStatusText.Text = $"'{state.Name}' 상태를 편집 중입니다.";
        StateStatusText.Foreground = BrushFromHex("#B8EAF5");
    }

    private void ClearEditor()
    {
        _editingStateId = null;
        _isRefreshing = true;
        StateListBox.SelectedItem = null;
        _isRefreshing = false;
        StateNameTextBox.Clear();
        StateKindComboBox.SelectedItem = CharacterLetterStateKinds.Conditional;
        StateLettersTextBox.Clear();
        StateMergeModeComboBox.SelectedItem = AddModeText;
        StateNoteTextBox.Clear();
        UpdateStateButton.IsEnabled = false;
        StateNameTextBox.Focus();
        StateStatusText.Text = "새 문자 상태의 이름과 문자를 입력하세요.";
        StateStatusText.Foreground = BrushFromHex("#AEB8C8");
    }

    private void NewStateButton_Click(object sender, RoutedEventArgs e)
        => ClearEditor();

    private void AddStateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadEditorValues(
                out string name,
                out string kind,
                out bool includeBase,
                out List<string> letters,
                out string note))
        {
            return;
        }

        if (_states.Any(state =>
                string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            SetError("같은 이름의 문자 상태가 이미 있습니다.");
            return;
        }

        var state = new CharacterLetterState
        {
            Id = $"state-{Guid.NewGuid():N}",
            Name = name,
            Kind = kind,
            IncludeBaseLetters = includeBase,
            Letters = letters,
            Note = note
        };
        _states.Add(state);
        _editingStateId = state.Id;
        RefreshStateList(state.Id);
        BeginEditing(state.Id);
        StateStatusText.Text = $"'{state.Name}' 상태를 추가했습니다.";
        StateStatusText.Foreground = BrushFromHex("#8FE3B1");
    }

    private void UpdateStateButton_Click(object sender, RoutedEventArgs e)
    {
        CharacterLetterState? state = FindState(_editingStateId);
        if (state is null)
        {
            SetError("수정할 문자 상태를 먼저 선택하세요.");
            return;
        }

        if (!TryReadEditorValues(
                out string name,
                out string kind,
                out bool includeBase,
                out List<string> letters,
                out string note))
        {
            return;
        }

        if (_states.Any(other =>
                !string.Equals(other.Id, state.Id, StringComparison.Ordinal) &&
                string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            SetError("같은 이름의 다른 문자 상태가 이미 있습니다.");
            return;
        }

        state.Name = name;
        state.Kind = kind;
        state.IncludeBaseLetters = includeBase;
        state.Letters = letters;
        state.Note = note;
        RefreshStateList(state.Id);
        BeginEditing(state.Id);
        StateStatusText.Text = $"'{state.Name}' 상태를 수정했습니다.";
        StateStatusText.Foreground = BrushFromHex("#8FE3B1");
    }

    private void DeleteStateButton_Click(object sender, RoutedEventArgs e)
    {
        CharacterLetterState? state = FindState(_editingStateId);
        if (state is null)
        {
            SetError("삭제할 문자 상태를 먼저 선택하세요.");
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            $"'{state.Name}' 문자 상태를 삭제할까요?",
            "문자 상태 삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _states.Remove(state);
        ClearEditor();
        RefreshStateList();
        StateStatusText.Text = $"'{state.Name}' 상태를 삭제했습니다.";
        StateStatusText.Foreground = BrushFromHex("#FFD08A");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitPendingChanges())
        {
            return;
        }

        SavedStates = DeckDataService.NormalizeLetterStates(_states)
            .Select(CharacterLibraryService.CloneState)
            .ToArray();
        DialogResult = true;
    }

    private bool TryCommitPendingChanges()
    {
        string rawName = StateNameTextBox.Text.Trim();
        string rawLetters = StateLettersTextBox.Text.Trim();
        if (rawName.Length == 0 && rawLetters.Length == 0)
        {
            return true;
        }

        CharacterLetterState? editing = FindState(_editingStateId);
        if (editing is null)
        {
            if (!TryReadEditorValues(
                    out string name,
                    out string kind,
                    out bool includeBase,
                    out List<string> letters,
                    out string note))
            {
                return false;
            }

            if (_states.Any(state =>
                    string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                SetError("입력 중인 새 상태가 기존 상태와 중복됩니다.");
                return false;
            }

            _states.Add(new CharacterLetterState
            {
                Id = $"state-{Guid.NewGuid():N}",
                Name = name,
                Kind = kind,
                IncludeBaseLetters = includeBase,
                Letters = letters,
                Note = note
            });
            return true;
        }

        List<string> parsedLetters = ParseLetters(rawLetters);
        string selectedKind = CharacterLetterStateKinds.Normalize(StateKindComboBox.SelectedItem as string);
        bool selectedIncludeBase = string.Equals(
            StateMergeModeComboBox.SelectedItem as string,
            AddModeText,
            StringComparison.Ordinal);
        string selectedNote = StateNoteTextBox.Text.Trim();

        bool changed = !string.Equals(editing.Name, rawName, StringComparison.Ordinal) ||
                       !string.Equals(editing.Kind, selectedKind, StringComparison.Ordinal) ||
                       editing.IncludeBaseLetters != selectedIncludeBase ||
                       !editing.Letters.SequenceEqual(parsedLetters, StringComparer.Ordinal) ||
                       !string.Equals(editing.Note, selectedNote, StringComparison.Ordinal);
        if (!changed)
        {
            return true;
        }

        if (!TryReadEditorValues(
                out string updatedName,
                out string updatedKind,
                out bool updatedIncludeBase,
                out List<string> updatedLetters,
                out string updatedNote))
        {
            return false;
        }

        if (_states.Any(other =>
                !string.Equals(other.Id, editing.Id, StringComparison.Ordinal) &&
                string.Equals(other.Name, updatedName, StringComparison.OrdinalIgnoreCase)))
        {
            SetError("같은 이름의 다른 문자 상태가 이미 있습니다.");
            return false;
        }

        editing.Name = updatedName;
        editing.Kind = updatedKind;
        editing.IncludeBaseLetters = updatedIncludeBase;
        editing.Letters = updatedLetters;
        editing.Note = updatedNote;
        return true;
    }

    private bool TryReadEditorValues(
        out string name,
        out string kind,
        out bool includeBase,
        out List<string> letters,
        out string note)
    {
        name = StateNameTextBox.Text.Trim();
        kind = CharacterLetterStateKinds.Normalize(StateKindComboBox.SelectedItem as string);
        includeBase = string.Equals(
            StateMergeModeComboBox.SelectedItem as string,
            AddModeText,
            StringComparison.Ordinal);
        letters = ParseLetters(StateLettersTextBox.Text);
        note = StateNoteTextBox.Text.Trim();

        if (name.Length == 0)
        {
            SetError("상태 이름을 입력하세요.");
            StateNameTextBox.Focus();
            return false;
        }

        if (letters.Count == 0)
        {
            SetError("상태 문자를 한 글자 이상 입력하세요.");
            StateLettersTextBox.Focus();
            return false;
        }

        return true;
    }

    private static List<string> ParseLetters(string text)
    {
        string normalized = (text ?? string.Empty).Normalize(NormalizationForm.FormC);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            string value = rune.ToString();
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);

            if (Rune.IsWhiteSpace(rune) ||
                value is "," or "，" or "、" or "/" or "|" or "·" or "・" or ";" or "；")
            {
                continue;
            }

            if (category == UnicodeCategory.NonSpacingMark && result.Count > 0)
            {
                string combined = (result[^1] + value).Normalize(NormalizationForm.FormC);
                seen.Remove(result[^1]);
                result[^1] = combined;
                seen.Add(combined);
                continue;
            }

            string cell = KanaUtility.NormalizeCell(value);
            if (cell.Length > 0 && seen.Add(cell))
            {
                result.Add(cell);
            }
        }

        return result;
    }

    private CharacterLetterState? FindState(string? stateId)
        => string.IsNullOrWhiteSpace(stateId)
            ? null
            : _states.FirstOrDefault(state =>
                string.Equals(state.Id, stateId, StringComparison.Ordinal));

    private void SetError(string message)
    {
        StateStatusText.Text = message;
        StateStatusText.Foreground = BrushFromHex("#FF9E9E");
    }

    private static SolidColorBrush BrushFromHex(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));

    public sealed class StateDisplayItem
    {
        public StateDisplayItem(CharacterLetterState state)
        {
            Id = state.Id;
            Name = state.Name;
            Kind = CharacterLetterStateKinds.Normalize(state.Kind);
            ModeText = state.IncludeBaseLetters
                ? "기본 문자 + 상태 문자"
                : "상태 문자로 대체";
            LettersText = string.Join(" · ", state.Letters);
            Note = state.Note;
        }

        public string Id { get; }
        public string Name { get; }
        public string Kind { get; }
        public string ModeText { get; }
        public string LettersText { get; }
        public string Note { get; }
    }
}
