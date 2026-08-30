using System.IO;
using System.Text.Json;
using KotodamanWordFinder.Models;

namespace KotodamanWordFinder.Services;

// characters.json에 필드를 추가하지 않고 별도 조회 파일로 캐릭터/그룹 이름의
// 한국어 표시명을 관리한다. characters.json은 DataUpdateService의 델타 업데이트·
// 3-way 병합 파이프라인이 계속 건드리는 살아있는 파일이라, 새 필드를 넣으면
// 그 파이프라인 전체가 이 필드를 알아야 하기 때문이다. en/ja는 원본
// Name/GroupName(일본어) 그대로 사용하므로 조회 파일이 없어도 동작한다.
public static class CharacterNameLoc
{
    private static IReadOnlyDictionary<string, string> _characterNames =
        new Dictionary<string, string>();
    private static IReadOnlyDictionary<string, string> _groupNames =
        new Dictionary<string, string>();

    public static void Load()
    {
        _characterNames = LoadMap(AppPaths.GetBundledDataPath("character_names.ko.json"));
        _groupNames = LoadMap(AppPaths.GetBundledDataPath("group_names.ko.json"));
    }

    public static string GetName(CharacterEntry character)
        => GetName(character.Id, character.Name);

    // CharacterAssignment 등 검색 결과 캐시는 CharacterEntry 전체가 아니라
    // Id/원문 이름만 들고 있어서, 그 자리에서 바로 쓸 수 있게 분리해뒀다.
    public static string GetName(string characterId, string fallbackName)
        => Loc.CurrentLanguage == "ko" && _characterNames.TryGetValue(characterId, out string? name)
            ? name
            : fallbackName;

    public static string GetGroupName(string groupName)
        => Loc.CurrentLanguage == "ko" && _groupNames.TryGetValue(groupName, out string? name)
            ? name
            : groupName;

    private static IReadOnlyDictionary<string, string> LoadMap(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>();
        }
        catch
        {
            // 조회 파일이 손상돼도 원문(일본어) 이름으로 계속 동작해야 한다.
            return new Dictionary<string, string>();
        }
    }
}
