using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MCCX_App.Converters;

/// <summary>
/// bool → Visibility：账号列表为空（没有选中账号）时把右侧“窗口”整体折叠成空白页。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}
