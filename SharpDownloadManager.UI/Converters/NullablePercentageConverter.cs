using System;
using System.Globalization;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;

namespace SharpDownloadManager.UI.Converters;

public sealed class NullablePercentageConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && !double.IsNaN(d))
        {
            var clamped = Math.Clamp(d, 0d, 100d);
            return clamped.ToString("0.0", culture) + "%";
        }

        return "—";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
