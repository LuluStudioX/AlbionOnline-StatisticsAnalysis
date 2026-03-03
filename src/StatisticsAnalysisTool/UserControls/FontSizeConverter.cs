using System;
using System.Globalization;
using System.Windows.Data;

namespace StatisticsAnalysisTool.UserControls
{
    public class FontSizeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // Expect variable binding counts. First value should be SelectedSectionIndex (int or double)
            if (values == null || values.Length == 0)
                return 16.0;

            int selectedIndex = 0;
            try
            {
                if (values[0] is int i) selectedIndex = i;
                else if (values[0] is double d) selectedIndex = (int) d;
                else if (values[0] is string s && int.TryParse(s, out var p)) selectedIndex = p;
            }
            catch { selectedIndex = 0; }

            // Collect all numeric font/icon size values from the remaining bindings in order
            var doubles = new System.Collections.Generic.List<double>();
            for (int idx = 1; idx < values.Length; idx++)
            {
                if (values[idx] is double dd) doubles.Add(dd);
                else if (values[idx] is int ii) doubles.Add(ii);
                else if (values[idx] is float ff) doubles.Add(ff);
                else if (values[idx] is string ss && double.TryParse(ss, out var parsed)) doubles.Add(parsed);
            }

            if (doubles.Count == 0) return 16.0;

            // Mapping strategy (best-effort): values after SelectedIndex usually come in this order:
            // [Dashboard, (Gathering), Damage, Repair] but Gathering may be omitted in many templates.
            // We'll choose the most likely candidate based on selectedIndex and available values.
            switch (selectedIndex)
            {
                case 0: // Dashboard -> prefer first numeric value
                    return doubles[0];
                case 1: // Gathering -> prefer first numeric if only one present, otherwise try to pick a middle value
                    if (doubles.Count == 1) return doubles[0];
                    // if 2 values, assume [Dashboard, Damage] -> fall back to Dashboard size for gathering
                    return doubles[0];
                case 2: // Damage -> prefer second numeric if present, otherwise first
                    return (doubles.Count >= 2) ? doubles[1] : doubles[0];
                case 3: // Repair -> prefer third numeric if present, else last available
                    if (doubles.Count >= 3) return doubles[2];
                    return doubles[doubles.Count - 1];
                default:
                    return doubles[0];
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}