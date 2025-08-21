using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace StatisticsAnalysisTool.UserControls
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = false;
            if (value is bool b)
                boolValue = b;
            bool invert = parameter != null && parameter.ToString().Equals("Invert", StringComparison.OrdinalIgnoreCase);
            if (invert)
                boolValue = !boolValue;
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility v)
            {
                bool invert = parameter != null && parameter.ToString().Equals("Invert", StringComparison.OrdinalIgnoreCase);
                bool result = v == Visibility.Visible;
                return invert ? !result : result;
            }
            return false;
        }
    }
}
