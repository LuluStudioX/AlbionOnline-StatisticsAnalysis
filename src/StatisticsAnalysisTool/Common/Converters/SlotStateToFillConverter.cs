using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace StatisticsAnalysisTool.Common.Converters;

public class SlotStateToFillConverter : IValueConverter
{
    private static readonly SolidColorBrush House      = new(Color.FromRgb(100, 149, 237));
    private static readonly SolidColorBrush Farm       = new(Color.FromRgb(144, 238, 144));
    private static readonly SolidColorBrush HerbGarden = new(Color.FromRgb(60,  179, 113));
    private static readonly SolidColorBrush Pasture    = new(Color.FromRgb(245, 222, 179));
    private static readonly SolidColorBrush Kennel     = new(Color.FromRgb(210, 180, 140));
    private static readonly SolidColorBrush Sent       = new(Color.FromRgb(255, 165,   0));
    private static readonly SolidColorBrush LootReady  = new(Color.FromRgb(255, 215,   0));
    private static readonly SolidColorBrush OnJob      = new(Color.FromRgb(70,  130, 180));
    private static readonly SolidColorBrush Home       = new(Color.FromRgb(152, 251, 152));
    private static readonly SolidColorBrush Empty      = new(Color.FromArgb(102, 64,  64,  64));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "house"      => House,
            "farm"       => Farm,
            "herbgarden" => HerbGarden,
            "pasture"    => Pasture,
            "kennel"     => Kennel,
            "sent"       => Sent,
            "loot_ready" => LootReady,
            "on_job"     => OnJob,
            "home"       => Home,
            _            => Empty
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
