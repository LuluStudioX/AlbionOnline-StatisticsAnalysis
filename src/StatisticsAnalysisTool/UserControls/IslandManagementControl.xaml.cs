using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Localization;
using StatisticsAnalysisTool.Models.BindingModel;
using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.Views;
using StatisticsAnalysisTool.ViewModels;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Linq;

namespace StatisticsAnalysisTool.UserControls;

public partial class IslandManagementControl : UserControl
{
    public IslandManagementControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        IslandMapViewControl.RefreshMapOverlay(PlotsMapOverlayCanvas, PlotsMapImage);
        ServiceLocator.Resolve<TrackingController>()?.IslandController?.StartCountdownTimer();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ServiceLocator.Resolve<TrackingController>()?.IslandController?.StopCountdownTimer();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainWindowViewModel oldVm)
        {
            oldVm.IslandBindings.PropertyChanged -= OnIslandBindingsPropertyChanged;
            UnsubscribeLaborerUpdates(oldVm);
        }
        if (e.NewValue is MainWindowViewModel newVm)
        {
            newVm.IslandBindings.PropertyChanged += OnIslandBindingsPropertyChanged;
            SubscribeLaborerUpdates(newVm);
        }
    }

    private void SubscribeLaborerUpdates(MainWindowViewModel vm)
    {
        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        if (controller == null) return;
        controller.LaborerSnapshotsChanged += OnLaborerSnapshotsChanged;
    }

    private void UnsubscribeLaborerUpdates(MainWindowViewModel vm)
    {
        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        if (controller == null) return;
        controller.LaborerSnapshotsChanged -= OnLaborerSnapshotsChanged;
    }

    private void OnLaborerSnapshotsChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            IslandLaborerViewControl.RefreshFromViewModel();
            IslandMapViewControl.RefreshMapOverlay(PlotsMapOverlayCanvas, PlotsMapImage);
        });
    }

    private void OnIslandBindingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.BindingModel.IslandBindings.SelectedIsland))
        {
            IslandMapViewControl.RefreshFromViewModel();
            IslandLaborerViewControl.RefreshFromViewModel();
            IslandMapViewControl.RefreshMapOverlay(PlotsMapOverlayCanvas, PlotsMapImage);
        }
    }

    private void OwnerOverviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var window = new Views.OwnerOverviewWindow(vm) { Owner = Window.GetWindow(this) };
        window.Show();
    }

    private void EditPlotInline_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var entry = vm.IslandBindings.SelectedPlot;
        var islandId = vm.IslandBindings.SelectedIsland?.IslandId ?? Guid.Empty;
        if (entry == null || islandId == Guid.Empty) return;

        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        var island = controller?.GetById(islandId);
        var plot = island?.Plots.FirstOrDefault(p => p.Id == entry.PlotId);
        if (plot == null) return;

        var dialog = new Views.AddEditPlotWindow(plot) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result != null)
            vm.IslandBindings.CommitEditPlot(islandId, dialog.Result);
    }

    private void ToggleSettingsPanel_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.IsSettingsPanelOpen = !vm.IslandBindings.IsSettingsPanelOpen;
    }

    private void AddIslandButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var prefill = vm.IslandBindings.BuildAddIslandPrefill();
        var dialog = new AddEditIslandWindow(prefill, isEdit: false) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result != null)
            vm.IslandBindings.CommitAddIsland(dialog.Result);
    }

    private void EditIslandButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var entry = vm.IslandBindings.SelectedIsland;
        if (entry == null) return;
        var dialog = new AddEditIslandWindow(entry) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        vm.IslandBindings.CommitEditIsland(entry, dialog.Result, dialog.DeleteRequested);
    }

    private void ResetSlotsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var island = vm.IslandBindings.SelectedIsland;
        if (island == null) return;
        vm.IslandBindings.ResetSlotAssignments();
        MessageBox.Show(
            LocalizationController.Translation("ISLAND_MANAGEMENT_SLOTS_RESET_MESSAGE",
                new List<string> { "islandName" }, new List<string> { island.Name }),
            LocalizationController.Translation("ISLAND_MANAGEMENT_SLOTS_RESET_TITLE"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void PlotItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not FrameworkElement { DataContext: IslandPlotEntry entry }) return;
        vm.IslandBindings.SelectedPlot = vm.IslandBindings.SelectedPlot?.PlotId == entry.PlotId ? null : entry;
    }

    private void AddPlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var islandId = vm.IslandBindings.SelectedIsland?.IslandId;
        if (islandId == null) return;
        var dialog = new AddEditPlotWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Result != null)
            vm.IslandBindings.CommitAddPlot(islandId.Value, dialog.Result);
    }

    private void DeletePlotButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var islandId = vm.IslandBindings.SelectedIsland?.IslandId;
        var plotId = vm.IslandBindings.SelectedPlot?.PlotId;
        if (islandId == null || plotId == null) return;
        vm.IslandBindings.CommitDeletePlot(islandId.Value, plotId.Value);
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not FrameworkElement { DataContext: IslandEntry island }) return;
        DragDrop.DoDragDrop((DependencyObject) sender, island, DragDropEffects.Move);
    }

    private void IslandListBox_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!e.Data.GetDataPresent(typeof(IslandEntry))) return;

        var dragged = (IslandEntry) e.Data.GetData(typeof(IslandEntry));
        var target = GetIslandEntryUnderPoint(e.GetPosition(IslandListBox));
        vm.IslandBindings.MoveIsland(dragged, target);
    }

    private void StartAllCyclesButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var islandId = vm.IslandBindings.SelectedIsland?.IslandId;
        if (islandId == null) return;
        vm.IslandBindings.CommitStartAllCycles(islandId.Value);
    }

    private IslandEntry GetIslandEntryUnderPoint(System.Windows.Point point)
    {
        var hit = IslandListBox.InputHitTest(point) as DependencyObject;
        while (hit != null)
        {
            if (hit is FrameworkElement { DataContext: IslandEntry island })
                return island;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return null;
    }

    // ── Yield tab ─────────────────────────────────────────────────────────────

    private void BtnClearIslandYield_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.ClearIslandYield();
    }

}
