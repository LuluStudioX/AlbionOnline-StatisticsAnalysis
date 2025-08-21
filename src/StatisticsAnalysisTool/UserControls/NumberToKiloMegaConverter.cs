using System;
using System.Globalization;
using System.Windows.Data;

namespace StatisticsAnalysisTool.UserControls
{
    public class NumberToKiloMegaConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "";
            if (value is string s && double.TryParse(s, out double d))
                value = d;
            if (value is double dbl)
            {
                if (dbl >= 1_000_000)
                    return string.Format("{0:0.#}M", dbl / 1_000_000);
                if (dbl >= 1_000)
                    return string.Format("{0:0.#}K", dbl / 1_000);
                return dbl.ToString("N0");
            }
            if (value is int i)
            {
                if (i >= 1_000_000)
                    return string.Format("{0:0.#}M", i / 1_000_000.0);
                if (i >= 1_000)
                    return string.Format("{0:0.#}K", i / 1_000.0);
                return i.ToString("N0");
            }
            return value.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
