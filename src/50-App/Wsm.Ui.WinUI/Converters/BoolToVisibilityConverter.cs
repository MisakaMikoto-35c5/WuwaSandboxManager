using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Wsm.Ui.WinUI.Converters;

/// <summary>
/// Maps a boolean to <see cref="Visibility"/>. Pass <c>ConverterParameter=Invert</c>
/// to show the element when the value is false.
/// </summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
