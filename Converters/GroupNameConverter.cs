using System.Globalization;
using System.Windows.Data;
using KotodamanWordFinder.Services;

namespace KotodamanWordFinder.Converters;

// 필터 콤보박스의 표시 전용 변환기. ItemsSource/SelectedItem은 원문(일본어) 그룹명
// 그대로 두고(필터 비교·설정 저장이 이 값을 그대로 쓴다), 화면에 그리는 텍스트만
// 한국어로 바꾼다 - 값과 표시를 분리해 콤보의 필터링/저장 로직은 건드리지 않는다.
public sealed class GroupNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string groupName ? CharacterNameLoc.GetGroupName(groupName) : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
