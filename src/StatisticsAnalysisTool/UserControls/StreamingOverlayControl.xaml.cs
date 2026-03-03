using System;
using System.Windows.Controls;
using System.Windows;
using Serilog;

namespace StatisticsAnalysisTool.UserControls;

public partial class StreamingOverlayControl : UserControl
{
    public StreamingOverlayControl()
    {
        InitializeComponent();
        // Prefer resolving the registered VM from the ServiceLocator (ensures we get the app instance)
        try
        {
            Log.Information("[OverlayControl] Checking ServiceLocator for StreamingOverlayViewModel registration");
            if (StatisticsAnalysisTool.Common.ServiceLocator.IsServiceInDictionary<StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel>())
            {
                var resolved = StatisticsAnalysisTool.Common.ServiceLocator.Resolve<StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel>();
                Log.Information("[OverlayControl] Resolved StreamingOverlayViewModel from ServiceLocator: {Type}", resolved?.GetType().FullName ?? "null");
                this.DataContext = resolved;
                return;
            }
            Log.Information("[OverlayControl] StreamingOverlayViewModel not found in ServiceLocator");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OverlayControl] Error while resolving StreamingOverlayViewModel from ServiceLocator");
        }

        // Fallback: use singleton Instance if available, otherwise create a local VM and attempt to swap on Loaded
        var singleton = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.Instance;
        if (singleton != null)
        {
            Log.Information("[OverlayControl] Using singleton Instance for DataContext");
            this.DataContext = singleton;
        }
        else
        {
            // Do not create a new ViewModel here; wait for the application to register the shared instance.
            Log.Information("[OverlayControl] No singleton Instance available yet; deferring DataContext until Loaded");
            this.DataContext = null;
            this.Loaded += StreamingOverlayControl_Loaded;
        }
    }

    private void StreamingOverlayControl_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var singleton = StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel.Instance;
            if (singleton != null)
            {
                Log.Information("[OverlayControl] Loaded: assigning DataContext from singleton Instance");
                this.DataContext = singleton;
                this.Loaded -= StreamingOverlayControl_Loaded;
            }
            else
            {
                Log.Information("[OverlayControl] Loaded: singleton Instance still not available");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[OverlayControl] Error during Loaded handler");
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
