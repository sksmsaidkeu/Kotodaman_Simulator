using System.Text.Json.Serialization;
using KotodamanWordFinder.Services;

namespace KotodamanWordFinder.Models;

public sealed class DataVersionMetadata
{
    public int SchemaVersion { get; set; } = 1;
    public string DataVersion { get; set; } = string.Empty;
    public string MinimumAppVersion { get; set; } = AppPaths.AppVersion;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public int CharacterCount { get; set; }
}

public sealed class DataUpdateManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string PackageType { get; set; } = "KotodamanDataUpdate";
    public string FromDataVersion { get; set; } = string.Empty;
    public string DataVersion { get; set; } = string.Empty;
    public string MinimumAppVersion { get; set; } = AppPaths.AppVersion;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int AddedCharacterCount { get; set; }
    public int UpdatedCharacterCount { get; set; }
    public int DeletedCharacterCount { get; set; }
    public List<DataUpdateImageChange> Images { get; set; } = new();
    public List<DataUpdateReferenceChange> References { get; set; } = new();
}

public sealed class DataUpdateCharacterChange
{
    public string ChangeType { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public CharacterEntry? Previous { get; set; }
    public CharacterEntry? Current { get; set; }
}

public sealed class DataUpdateImageChange
{
    public string ChangeType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;
    public string? PreviousSha256 { get; set; }
    public string? CurrentSha256 { get; set; }
}

// DeckScreenshotLearningService가 쓰는 인식 학습 참조 이미지입니다.
// CharacterImages와 달리 "<UI 프로필>/<캐릭터>/slot-*.png" 하위 폴더 구조라
// 파일 이름 하나가 아니라 상대 경로 전체로 식별합니다.
public sealed class DataUpdateReferenceChange
{
    public string ChangeType { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string ArchivePath { get; set; } = string.Empty;
    public string? PreviousSha256 { get; set; }
    public string? CurrentSha256 { get; set; }
}
