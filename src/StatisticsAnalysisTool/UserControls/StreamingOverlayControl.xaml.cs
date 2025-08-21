using System;
using System.Windows.Controls;

namespace StatisticsAnalysisTool.UserControls;

public partial class StreamingOverlayControl : UserControl
{
    public StreamingOverlayControl()
    {
        InitializeComponent();
        // Prefer resolving the registered VM from the ServiceLocator (ensures we get the app instance)
        if (StatisticsAnalysisTool.Common.ServiceLocator.IsServiceInDictionary<StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel>())
        {
            this.DataContext = StatisticsAnalysisTool.Common.ServiceLocator.Resolve<StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel>();
            return;
        }

        // Fallback: use singleton Instance if available, otherwise create a local VM and attempt to swap on Loaded
        var singleton = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.Instance;
        if (singleton != null)
        {
            this.DataContext = singleton;
        }
        else
        {
            // Do not create a new ViewModel here; wait for the application to register the shared instance.
            this.DataContext = null;
            this.Loaded += StreamingOverlayControl_Loaded;
        }
    }



    private void StreamingOverlayControl_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        var singleton = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.Instance;
        if (singleton != null)
        {
            this.DataContext = singleton;
            this.Loaded -= StreamingOverlayControl_Loaded;
        }
    }

    private void OverlayEnableToggle_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            if (this.DataContext is StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel vm)
            {
                vm.IsOverlayEnabled = !vm.IsOverlayEnabled;
            }
        }
        catch { }
    }

    private void ToggleGathering_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            if (this.DataContext is StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel vm)
                vm.ShowGathering = !vm.ShowGathering;
        }
        catch { }
    }

    private void ToggleDamage_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            if (this.DataContext is StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel vm)
                vm.ShowDamage = !vm.ShowDamage;
        }
        catch { }
    }

    private void ToggleRepair_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            if (this.DataContext is StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel vm)
                vm.ShowRepair = !vm.ShowRepair;
        }
        catch { }
    }

    private void ToggleDashboard_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            if (this.DataContext is StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel vm)
                vm.ShowDashboard = !vm.ShowDashboard;
        }
        catch { }
    }
}
