namespace KotodamanWordFinder.Models;

public sealed class MiracleLeaderEffect
{
    // 이 캐릭터가 덱 1번(리더)일 때만 적용됩니다.
    public bool IsEnabled { get; set; }

    // 효과 설명에서 같은 대상으로 취급되는 그룹을 모두 넣습니다.
    public List<string> TargetGroups { get; set; } = new();

    // 대상 그룹 캐릭터들에게 추가되는 문자입니다.
    public List<string> GrantedLetters { get; set; } = new();

    // 발동 조건이나 원문 메모를 남길 수 있습니다.
    public string Note { get; set; } = string.Empty;

    public bool IsConfigured
        => IsEnabled &&
           TargetGroups is { Count: > 0 } &&
           GrantedLetters is { Count: > 0 };
}
