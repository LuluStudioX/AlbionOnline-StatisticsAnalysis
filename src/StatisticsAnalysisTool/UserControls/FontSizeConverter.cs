using System;
using System.Globalization;
using System.Windows.Data;

namespace StatisticsAnalysisTool.UserControls
{
    public class FontSizeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 3 && values[0] is int selectedSectionIndex)
            {
                // Section indices: 0 = Dashboard, 1 = Gathering, 2 = DamageMeter
                switch (selectedSectionIndex)
                {
                    case 0: // Dashboard
                        if (values[1] is double dashboardFontSize)
                            return dashboardFontSize;
                        break;
                    case 2: // Damage Meter
                        if (values[2] is double damageFontSize)
                            return damageFontSize;
                        break;
                }
            }

            // Default fallback
            return 16.0;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}