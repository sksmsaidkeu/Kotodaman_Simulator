using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KotodamanWordFinder.Models;

public sealed class CharacterImportReviewItem : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private bool _isFavorite;
    private bool _useImage = true;
    private string _name = string.Empty;
    private string _category = CharacterCategories.Other;
    private string _attribute = string.Empty;
    private string _subAttributesText = string.Empty;
    private string _species = string.Empty;
    private string _groupName = string.Empty;
    private string _lettersText = string.Empty;
    private string _includedGroupsText = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetField(ref _isFavorite, value);
    }

    public bool UseImage
    {
        get => _useImage;
        set
        {
            if (SetField(ref _useImage, value))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetField(ref _name, value ?? string.Empty))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public string Category
    {
        get => _category;
        set => SetField(ref _category, value ?? CharacterCategories.Other);
    }

    public string Attribute
    {
        get => _attribute;
        set
        {
            if (SetField(ref _attribute, value ?? string.Empty))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public string SubAttributesText
    {
        get => _subAttributesText;
        set => SetField(ref _subAttributesText, value ?? string.Empty);
    }

    public string Species
    {
        get => _species;
        set
        {
            if (SetField(ref _species, value ?? string.Empty))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public string GroupName
    {
        get => _groupName;
        set
        {
            if (SetField(ref _groupName, value ?? string.Empty))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public string LettersText
    {
        get => _lettersText;
        set
        {
            if (SetField(ref _lettersText, value ?? string.Empty))
            {
                NotifyReviewStateChanged();
            }
        }
    }

    public string IncludedGroupsText
    {
        get => _includedGroupsText;
        set => SetField(ref _includedGroupsText, value ?? string.Empty);
    }

    public string ImageUrl { get; set; } = string.Empty;
    public string DownloadedImagePath { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceSite { get; set; } = string.Empty;
    public string MatchedDatabaseUrl { get; set; } = string.Empty;
    public string GameWithRatingText { get; set; } = string.Empty;
    public List<string> Notes { get; set; } = new();

    public bool NeedsReview =>
        string.IsNullOrWhiteSpace(Name) ||
        string.IsNullOrWhiteSpace(LettersText) ||
        string.IsNullOrWhiteSpace(Attribute) ||
        string.IsNullOrWhiteSpace(Species) ||
        (UseImage && ImageUrl.Length > 0 && string.IsNullOrWhiteSpace(DownloadedImagePath));

    public string ReviewStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                return "⚠ 이름 확인";
            }
            if (string.IsNullOrWhiteSpace(LettersText))
            {
                return "⚠ 문자 확인";
            }
            if (string.IsNullOrWhiteSpace(Attribute) || string.IsNullOrWhiteSpace(Species))
            {
                return "⚠ 속성/종족";
            }
            if (UseImage && ImageUrl.Length > 0 && string.IsNullOrWhiteSpace(DownloadedImagePath))
            {
                return "⚠ 이미지 확인";
            }
            return "OK";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void NotifyReviewStateChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsReview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReviewStatus)));
    }
}
