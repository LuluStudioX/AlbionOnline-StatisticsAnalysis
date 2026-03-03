using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace StatisticsAnalysisTool.UserControls
{
    /// <summary>
    /// Determines whether the metric image should be visible in the preview for repair section.
    /// Inputs (MultiBinding): [0] ShowImage (bool), [1] AlternationIndex (int), [2] SelectedSectionIndex (int)
    /// Visible when ShowImage==true AND (SelectedSectionIndex != 3 OR AlternationIndex == 0)
    /// </summary>
    public class RepairIconVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                bool showImage = true;
                int alternation = 0;
                int selectedIndex = -1;
                if (values.Length > 0 && values[0] is bool b) showImage = b;
                if (values.Length > 1 && values[1] is int a) alternation = a;
                if (values.Length > 2 && values[2] is int s) selectedIndex = s;

                if (!showImage) return Visibility.Hidden;
                // If not in Repair tab (index 3), always show image
                if (selectedIndex != 3) return Visibility.Visible;
                // In Repair tab: only show image for first item (alternation index 0)
                return alternation == 0 ? Visibility.Visible : Visibility.Hidden;
            }
            catch
            {
                return Visibility.Visible;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
