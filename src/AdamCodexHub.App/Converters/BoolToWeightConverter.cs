using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdamCodexHub.App.Converters;

/// <summary>
/// Maps a boolean to FontWeight: true → SemiBold, false → Normal. Used to make
/// the current nav item's label read heavier than the inactive ones.
/// </summary>
public sealed class BoolToWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? FontWeights.SemiBold : FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is FontWeight weight && weight == FontWeights.SemiBold;
}
