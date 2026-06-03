using System;
using System.Globalization;
using System.Windows.Data;

namespace StatisticsAnalysisTool.Common.Converters;

public class BoolToSlotSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 6.0 : 9.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
