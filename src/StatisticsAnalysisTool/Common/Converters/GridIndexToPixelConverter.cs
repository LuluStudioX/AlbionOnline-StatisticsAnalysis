using System;
using System.Globalization;
using System.Windows.Data;

namespace StatisticsAnalysisTool.Common.Converters;

public class GridIndexToPixelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int index) return 0.0;
        var cellSize = parameter is string s && int.TryParse(s, out var p) ? p : 12;
        return (double)(index * cellSize);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
