using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdamCodexHub.App.Converters;

/// <summary>
/// Inverts a boolean: true becomes false and vice versa. Used to enable/disable
/// the Close button while a model test is still running.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}
