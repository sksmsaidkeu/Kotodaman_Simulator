namespace KotodamanWordFinder.Models;

/// <summary>
/// 이름이 같은 모드시프트를 한 캐릭터 항목 안에서 관리하기 위한 추가 형태입니다.
/// 기본 형태는 CharacterEntry의 기존 이름/문자/이미지를 그대로 사용합니다.
/// </summary>
public sealed class CharacterForm
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ImageFileName { get; set; } = string.Empty;
    public List<string> Letters { get; set; } = new();
    // 비어 있으면 기본 형태의 메타데이터를 그대로 사용합니다.
    public string Attribute { get; set; } = string.Empty;
    public List<string> SubAttributes { get; set; } = new();
    public string Species { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}
