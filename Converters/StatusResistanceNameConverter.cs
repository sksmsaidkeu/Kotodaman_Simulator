using System.Globalization;
using System.Windows.Data;
using KotodamanWordFinder.Services;

namespace KotodamanWordFinder.Converters;

// GroupNameConverter와 같은 방식: ItemsSource/SelectedItem은 원문(일본어) 상태이상명
// 그대로 두고 화면 표시 텍스트만 한국어로 바꾼다.
public sealed class StatusResistanceNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string status ? CharacterNameLoc.GetStatusLabel(status) : value ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
