using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KotodamanWordFinder.Models;
using KotodamanWordFinder.Services;
using KotodamanWordFinder.Utilities;
using Microsoft.Win32;

namespace KotodamanWordFinder;

public partial class CharacterFormEditorWindow : Window
{
    private readonly string _characterId;
    private readonly string _dataDirectory;
    private readonly List<CharacterForm> _originalForms;
    private readonly List<FormEditItem> _items;
    private string? _editingFormId;
    private string? _pendingImageSourcePath;
    private bool _removeImageRequested;
    private bool _isRefreshing;

    public CharacterFormEditorWindow(
        string characterId,
        string characterName,
        string dataDirectory,
        IEnumerable<CharacterForm> forms)
    {
        InitializeComponent();

        _characterId = characterId;
        _dataDirectory = dataDirectory;
        _originalForms = DeckDataService.NormalizeCharacterForms(forms)
            .Select(CharacterLibraryService.CloneForm)
            .ToList();
        _items = _originalForms
            .Select(form => new FormEditItem(CharacterLibraryService.CloneForm(form)))
            .ToList();

        HeaderText.Text = $"{characterName} · 동일 이름 모드시프트";
        RefreshList(_items.FirstOrDefault()?.Form.Id);
        if (_items.Count == 0)
        {
            ClearEditorForNew();
        }
    }

    public IReadOnlyList<CharacterForm> SavedForms { get; private set; }
        = Array.Empty<CharacterForm>();

    private void RefreshList(string? selectedId = null)
    {
        _isRefreshing = true;
        FormListBox.ItemsSource = _items
            .Select(item => new FormDisplayItem(item.Form, GetItemThumbnail(item)))
            .ToArray();
        FormDisplayItem? selected = FormListBox.Items
            .OfType<FormDisplayItem>()
            .FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.Ordinal));
        FormListBox.SelectedItem = selected;
        _isRefreshing = false;

        if (selected is not null)
        {
            BeginEditing(selected.Id);
        }
    }


    private ImageSource? GetItemThumbnail(FormEditItem item)
    {
        if (item.RemoveImageRequested)
        {
            return null;
        }

        return !string.IsNullOrWhiteSpace(item.PendingImageSourcePath)
            ? CharacterImageService.LoadBitmapFromPath(item.PendingImageSourcePath, 96)
            : CharacterImageService.LoadBitmap(_dataDirectory, item.Form.ImageFileName, 96);
    }

    private void FormListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing || FormListBox.SelectedItem is not FormDisplayItem item)
        {
            return;
        }

        BeginEditing(item.Id);
    }

    private void BeginEditing(string formId)
    {
        FormEditItem? item = FindItem(formId);
        if (item is null)
        {
            return;
        }

        _editingFormId = item.Form.Id;
        FormNameTextBox.Text = item.Form.Name;
        FormLettersTextBox.Text = string.Join(" ", item.Form.Letters);
        FormAttributeTextBox.Text = DeckDataService.NormalizeAttribute(item.Form.Attribute);
        FormSubAttributesTextBox.Text = string.Join(" / ", DeckDataService.NormalizeAttributes(item.Form.SubAttributes, item.Form.Attribute));
        FormSpeciesTextBox.Text = DeckDataService.NormalizeSpecies(item.Form.Species);
        FormNoteTextBox.Text = item.Form.Note;
        _pendingImageSourcePath = item.PendingImageSourcePath;
        _removeImageRequested = item.RemoveImageRequested;
        UpdateImagePreview(item);
        ApplyFormButton.Content = "현재 형태 수정";
        EditorStatusText.Text = $"'{item.Form.Name}' 편집 중";
        EditorStatusText.Foreground = BrushFromHex("#B8EAF5");
    }

    private void ClearEditorForNew()
    {
        _editingFormId = null;
        _pendingImageSourcePath = null;
        _removeImageRequested = false;
        FormNameTextBox.Clear();
        FormLettersTextBox.Clear();
        FormAttributeTextBox.Clear();
        FormSubAttributesTextBox.Clear();
        FormSpeciesTextBox.Clear();
        FormNoteTextBox.Clear();
        FormImagePreview.Source = null;
        FormImagePreview.Visibility = Visibility.Collapsed;
        FormImagePlaceholder.Visibility = Visibility.Visible;
        FormImageFileText.Text = "등록된 이미지 없음";
        ApplyFormButton.Content = "새 형태 추가";
        EditorStatusText.Text = "형태 이름과 문자를 입력하세요.";
        EditorStatusText.Foreground = BrushFromHex("#AEB8C8");
        FormNameTextBox.Focus();
    }

    private void NewFormButton_Click(object sender, RoutedEventArgs e)
        => ClearEditorForNew();

    private void ApplyFormButton_Click(object sender, RoutedEventArgs e)
        => TryApplyEditorToItem(showSuccessMessage: true);

    private bool TryApplyEditorToItem(bool showSuccessMessage)
    {
        string name = FormNameTextBox.Text.Trim();
        List<string> letters = ParseLetters(FormLettersTextBox.Text);
        if (name.Length == 0)
        {
            SetError("형태 이름을 입력하세요.");
            return false;
        }
        if (letters.Count == 0)
        {
            SetError("형태에서 사용할 문자를 하나 이상 입력하세요.");
            return false;
        }

        bool duplicateName = _items.Any(item =>
            !string.Equals(item.Form.Id, _editingFormId, StringComparison.Ordinal) &&
            string.Equals(item.Form.Name, name, StringComparison.OrdinalIgnoreCase));
        if (duplicateName)
        {
            SetError("같은 이름의 형태가 이미 있습니다.");
            return false;
        }

        bool isNew = string.IsNullOrWhiteSpace(_editingFormId);
        FormEditItem? target = isNew
            ? new FormEditItem(new CharacterForm
            {
                Id = $"form-{Guid.NewGuid():N}"
            })
            : FindItem(_editingFormId);

        if (target is null)
        {
            SetError("편집 중인 형태를 찾지 못했습니다. 목록에서 다시 선택하세요.");
            return false;
        }

        if (isNew)
        {
            _items.Add(target);
            _editingFormId = target.Form.Id;
        }

        target.Form.Name = name;
        target.Form.Letters = letters;
        target.Form.Attribute = DeckDataService.NormalizeAttribute(FormAttributeTextBox.Text);
        target.Form.SubAttributes = ParseSubAttributes(FormSubAttributesTextBox.Text, target.Form.Attribute);
        target.Form.Species = DeckDataService.NormalizeSpecies(FormSpeciesTextBox.Text);
        target.Form.Note = FormNoteTextBox.Text.Trim();
        target.PendingImageSourcePath = _pendingImageSourcePath;
        target.RemoveImageRequested = _removeImageRequested;

        RefreshList(target.Form.Id);
        if (showSuccessMessage)
        {
            EditorStatusText.Text = isNew
                ? $"'{name}' 형태를 추가했습니다. 아래 저장하고 닫기를 눌러 확정하세요."
                : $"'{name}' 형태를 수정했습니다. 아래 저장하고 닫기를 눌러 확정하세요.";
            EditorStatusText.Foreground = BrushFromHex("#8FE3B1");
        }

        return true;
    }

    private void DeleteFormButton_Click(object sender, RoutedEventArgs e)
    {
        if (FormListBox.SelectedItem is not FormDisplayItem selected)
        {
            SetError("삭제할 형태를 선택하세요.");
            return;
        }

        FormEditItem? item = FindItem(selected.Id);
        if (item is null)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            $"'{item.Form.Name}' 형태를 삭제할까요?",
            "동일 이름 형태 삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _items.Remove(item);
        RefreshList(_items.FirstOrDefault()?.Form.Id);
        if (_items.Count == 0)
        {
            ClearEditorForNew();
        }
    }

    private void ChooseImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "형태 이미지 선택",
            Filter = CharacterImageService.GetDialogFilter(),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (CharacterImageService.LoadBitmapFromPath(dialog.FileName, 240) is null)
        {
            SetError("선택한 이미지를 읽을 수 없습니다.");
            return;
        }

        _pendingImageSourcePath = dialog.FileName;
        _removeImageRequested = false;
        FormImagePreview.Source = CharacterImageService.LoadBitmapFromPath(dialog.FileName, 240);
        FormImagePreview.Visibility = Visibility.Visible;
        FormImagePlaceholder.Visibility = Visibility.Collapsed;
        FormImageFileText.Text = Path.GetFileName(dialog.FileName) + " · 저장 시 PNG 변환";
    }

    private void RemoveImageButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingImageSourcePath = null;
        _removeImageRequested = true;
        FormImagePreview.Source = null;
        FormImagePreview.Visibility = Visibility.Collapsed;
        FormImagePlaceholder.Visibility = Visibility.Visible;
        FormImageFileText.Text = "이미지 제거 예정";
    }

    private void UpdateImagePreview(FormEditItem item)
    {
        ImageSource? source = null;
        if (!item.RemoveImageRequested)
        {
            source = !string.IsNullOrWhiteSpace(item.PendingImageSourcePath)
                ? CharacterImageService.LoadBitmapFromPath(item.PendingImageSourcePath, 240)
                : CharacterImageService.LoadBitmap(_dataDirectory, item.Form.ImageFileName, 240);
        }

        FormImagePreview.Source = source;
        FormImagePreview.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        FormImagePlaceholder.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
        FormImageFileText.Text = item.RemoveImageRequested
            ? "이미지 제거 예정"
            : !string.IsNullOrWhiteSpace(item.PendingImageSourcePath)
                ? Path.GetFileName(item.PendingImageSourcePath) + " · 저장 시 PNG 변환"
                : string.IsNullOrWhiteSpace(item.Form.ImageFileName)
                    ? "등록된 이미지 없음"
                    : item.Form.ImageFileName;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(FormNameTextBox.Text) ||
            !string.IsNullOrWhiteSpace(FormLettersTextBox.Text) ||
            !string.IsNullOrWhiteSpace(FormAttributeTextBox.Text) ||
            !string.IsNullOrWhiteSpace(FormSubAttributesTextBox.Text) ||
            !string.IsNullOrWhiteSpace(FormSpeciesTextBox.Text) ||
            !string.IsNullOrWhiteSpace(_editingFormId))
        {
            if (!TryApplyEditorToItem(showSuccessMessage: false))
            {
                return;
            }
        }

        try
        {
            List<CharacterForm> normalized = DeckDataService.NormalizeCharacterForms(
                _items.Select(item => item.Form));

            foreach (CharacterForm form in normalized)
            {
                FormEditItem? item = FindItem(form.Id);
                if (item is null)
                {
                    continue;
                }

                string previous = item.Form.ImageFileName;
                if (item.RemoveImageRequested)
                {
                    CharacterImageService.DeleteImage(_dataDirectory, previous);
                    form.ImageFileName = string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(item.PendingImageSourcePath))
                {
                    form.ImageFileName = CharacterImageService.SaveImageCopy(
                        item.PendingImageSourcePath,
                        _dataDirectory,
                        $"{_characterId}-{form.Id}",
                        previous);
                }
                else
                {
                    form.ImageFileName = Path.GetFileName(previous ?? string.Empty);
                }
            }

            var retainedIds = normalized.Select(form => form.Id).ToHashSet(StringComparer.Ordinal);
            foreach (CharacterForm original in _originalForms.Where(form => !retainedIds.Contains(form.Id)))
            {
                CharacterImageService.DeleteImage(_dataDirectory, original.ImageFileName);
            }

            SavedForms = normalized.Select(CharacterLibraryService.CloneForm).ToArray();
            DialogResult = true;
            Close();
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private FormEditItem? FindItem(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : _items.FirstOrDefault(item => string.Equals(item.Form.Id, id, StringComparison.Ordinal));

    private void SetError(string message)
    {
        EditorStatusText.Text = message;
        EditorStatusText.Foreground = BrushFromHex("#FF9E9E");
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
            if (Rune.IsWhiteSpace(rune) || value is "," or "，" or "、" or "/" or "|" or "·" or "・" or ";" or "；")
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

    private static List<string> ParseSubAttributes(string text, string? mainAttribute)
    {
        return DeckDataService.NormalizeAttributes(
            (text ?? string.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n', ',', '，', '、', '/', '／', '·', '・', '|', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            mainAttribute);
    }

    private static SolidColorBrush BrushFromHex(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));

    private sealed class FormEditItem
    {
        public FormEditItem(CharacterForm form)
        {
            Form = form;
        }

        public CharacterForm Form { get; }
        public string? PendingImageSourcePath { get; set; }
        public bool RemoveImageRequested { get; set; }
    }

    private sealed class FormDisplayItem
    {
        public FormDisplayItem(CharacterForm form, ImageSource? thumbnail)
        {
            Id = form.Id;
            Name = form.Name;
            string attribute = DeckDataService.NormalizeAttribute(form.Attribute);
            string attributeText = string.Join("/", new[] { attribute }
                .Concat(DeckDataService.NormalizeAttributes(form.SubAttributes, attribute))
                .Where(value => value.Length > 0));
            string species = DeckDataService.NormalizeSpecies(form.Species);
            string meta = string.Join(" · ", new[] { attributeText, species }.Where(value => value.Length > 0));
            LettersText = string.Join(" · ", form.Letters) + (meta.Length > 0 ? $"  |  {meta}" : string.Empty);
            Thumbnail = thumbnail;
        }

        public string Id { get; }
        public string Name { get; }
        public string LettersText { get; }
        public ImageSource? Thumbnail { get; }
        public Visibility ThumbnailVisibility => Thumbnail is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        public Visibility PlaceholderVisibility => Thumbnail is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
