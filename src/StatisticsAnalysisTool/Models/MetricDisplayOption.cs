using StatisticsAnalysisTool.ViewModels;

namespace StatisticsAnalysisTool.Models;

public class MetricDisplayOption : BaseViewModel
{
    private string _name = string.Empty;
    private string _icon = string.Empty;
    private string _color = "#FFFFFF";
    private bool _showTitle = true;
    private bool _showImage = true;
    private bool _showTotal = true;
    private bool _showPerHour = true;
    private bool _hideMetric = false;
    private string _totalValue = "0";
    private string _perHourValue = "0/h";

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public string Color
    {
        get => _color;
        set { _color = value; OnPropertyChanged(); }
    }

    public bool ShowTitle
    {
        get => _showTitle;
        set { _showTitle = value; OnPropertyChanged(); }
    }

    public bool ShowImage
    {
        get => _showImage;
        set { _showImage = value; OnPropertyChanged(); }
    }

    public bool ShowTotal
    {
        get => _showTotal;
        set { _showTotal = value; OnPropertyChanged(); }
    }

    public bool ShowPerHour
    {
        get => _showPerHour;
        set { _showPerHour = value; OnPropertyChanged(); }
    }

    public bool HideMetric
    {
        get => _hideMetric;
        set { _hideMetric = value; OnPropertyChanged(); }
    }

    public string TotalValue
    {
        get => _totalValue;
        set { _totalValue = value; OnPropertyChanged(); }
    }

    public string PerHourValue
    {
        get => _perHourValue;
        set { _perHourValue = value; OnPropertyChanged(); }
    }

    public bool IsDamagePreview { get; set; } = false;
    public string DpsValue { get; set; } = "0";
    public string HpsValue { get; set; } = "0";
    // PvP fields removed
    public double TitleFontSize { get; set; } = 14;
    public double ValueFontSize { get; set; } = 20;
    public double DpsFontSize { get; set; } = 12;
    public double HealFontSize { get; set; } = 12;
}