using System.Windows;
using StatisticsAnalysisTool.ViewModels;

namespace StatisticsAnalysisTool.Overlay;

public class OverlayDashboardBindings : BaseViewModel
{
    private Visibility _famePerHourVisibility = Visibility.Visible;
    private Visibility _silverPerHourVisibility = Visibility.Visible;
    private Visibility _reSpecPerHourVisibility = Visibility.Visible;
    private Visibility _mightPerHourVisibility = Visibility.Visible;
    private Visibility _favorPerHourVisibility = Visibility.Visible;

    public Visibility FamePerHourVisibility
    {
        get => _famePerHourVisibility;
        set { _famePerHourVisibility = value; OnPropertyChanged(); }
    }
    public Visibility SilverPerHourVisibility
    {
        get => _silverPerHourVisibility;
        set { _silverPerHourVisibility = value; OnPropertyChanged(); }
    }
    public Visibility ReSpecPerHourVisibility
    {
        get => _reSpecPerHourVisibility;
        set { _reSpecPerHourVisibility = value; OnPropertyChanged(); }
    }
    public Visibility MightPerHourVisibility
    {
        get => _mightPerHourVisibility;
        set { _mightPerHourVisibility = value; OnPropertyChanged(); }
    }
    public Visibility FavorPerHourVisibility
    {
        get => _favorPerHourVisibility;
        set { _favorPerHourVisibility = value; OnPropertyChanged(); }
    }
}
