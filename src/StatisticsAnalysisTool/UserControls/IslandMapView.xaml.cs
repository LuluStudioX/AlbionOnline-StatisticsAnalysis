using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Localization;
using StatisticsAnalysisTool.Models.BindingModel;
using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace StatisticsAnalysisTool.UserControls;

public partial class IslandMapView : UserControl
{
    private IslandLayoutDefinition _currentLayout;
    private IslandEntry _currentIsland;

    private double _zoomScale = 1.0;
    private const double ZoomStep = 0.25;
    private const double ZoomMin = 0.25;
    private const double ZoomMax = 4.0;

    private const double SlotDiameterLarge = 20;
    private const double SlotDiameterSmall = 14;

    private const string DragPlotFormat = "IslandPlotDrag";

    public IslandMapView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        if (controller != null)
            controller.LaborerSnapshotsChanged += OnLaborerSnapshotsChanged;
        RefreshFromViewModel();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        if (controller != null)
            controller.LaborerSnapshotsChanged -= OnLaborerSnapshotsChanged;
    }

    private void OnLaborerSnapshotsChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (DataContext is MainWindowViewModel vm)
                RebuildSlots(vm);
        });
    }

    public void RefreshFromViewModel()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var island = vm.IslandBindings.SelectedIsland;
        if (_currentIsland?.IslandId != island?.IslandId)
        {
            _currentIsland = island;

            if (island != null)
            {
                var domainIsland = ServiceLocator.Resolve<TrackingController>()?.IslandController?.GetById(island.IslandId);
                var (layout, imagePath) = IslandLayouts.ResolveForIsland(
                    domainIsland?.IslandType ?? IslandType.Player,
                    domainIsland?.City ?? string.Empty);
                _currentLayout = layout;
                ApplyLayout(layout, imagePath);
            }
            else
            {
                _currentLayout = null;
                ApplyLayout(null, null);
            }
        }

        RebuildSlots(vm);
        RebuildUnassignedChips();
    }

    private void ApplyLayout(IslandLayoutDefinition layout, string imagePath)
    {
        if (layout == null || string.IsNullOrWhiteSpace(imagePath))
        {
            MapScrollViewer.Visibility = Visibility.Collapsed;
            ZoomControls.Visibility = Visibility.Collapsed;
            NoLayoutPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            MapImage.Source = new BitmapImage(new Uri(imagePath));
            MapScrollViewer.Visibility = Visibility.Visible;
            ZoomControls.Visibility = Visibility.Visible;
            NoLayoutPlaceholder.Visibility = Visibility.Collapsed;
            ApplyZoom();
        }
        catch
        {
            MapScrollViewer.Visibility = Visibility.Collapsed;
            ZoomControls.Visibility = Visibility.Collapsed;
            NoLayoutPlaceholder.Visibility = Visibility.Visible;
        }
    }

    // ── Zoom ─────────────────────────────────────────────────────────────────

    private void ApplyZoom()
    {
        MapViewbox.LayoutTransform = new ScaleTransform(_zoomScale, _zoomScale);
    }

    private void ZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        _zoomScale = Math.Min(ZoomMax, _zoomScale + ZoomStep);
        ApplyZoom();
    }

    private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        _zoomScale = Math.Max(ZoomMin, _zoomScale - ZoomStep);
        ApplyZoom();
    }

    private void ZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        _zoomScale = 1.0;
        ApplyZoom();
    }

    private void MapScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        _zoomScale = e.Delta > 0
            ? Math.Min(ZoomMax, _zoomScale + ZoomStep)
            : Math.Max(ZoomMin, _zoomScale - ZoomStep);
        ApplyZoom();
    }

    // ── Plots-tab overlay ─────────────────────────────────────────────────────

    /// <summary>
    /// Populates an external Canvas + Image with the same slot circles as the Map tab.
    /// Called by IslandManagementControl when the selected island changes.
    /// </summary>
    public void RefreshMapOverlay(Canvas overlayCanvas, System.Windows.Controls.Image overlayImage)
    {
        overlayCanvas.Children.Clear();
        overlayImage.Source = null;

        if (_currentIsland == null || _currentLayout == null) return;

        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        var domainIsland = controller?.GetById(_currentIsland.IslandId);
        var plots = domainIsland?.Plots?.ToList() ?? new List<Island.IslandPlot>();

        // PNG background
        var (_, imagePath) = IslandLayouts.ResolveForIsland(
            domainIsland?.IslandType ?? IslandType.Player,
            domainIsland?.City ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            try { overlayImage.Source = new BitmapImage(new Uri(imagePath)); }
            catch { /* ignore */ }
        }

        // Slot circles (no interaction)
        foreach (var slot in _currentLayout.Slots)
        {
            var assignedPlot = plots.FirstOrDefault(p => p.MapSlotIndex == slot.SlotIndex);

            if (!slot.IsLarge && assignedPlot == null && _currentLayout.IsSmallSlotConsumedByPair(slot.SlotIndex, plots))
            {
                var consumedEllipse = new Ellipse
                {
                    Width = SlotDiameterSmall,
                    Height = SlotDiameterSmall,
                    Fill = new SolidColorBrush(Color.FromArgb(50, 200, 200, 200)),
                    Stroke = new SolidColorBrush(Color.FromArgb(80, 180, 180, 180)),
                    StrokeThickness = 1,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(consumedEllipse, slot.X - SlotDiameterSmall / 2);
                Canvas.SetTop(consumedEllipse, slot.Y - SlotDiameterSmall / 2);
                overlayCanvas.Children.Add(consumedEllipse);
                continue;
            }

            var isSpanningLarge = !slot.IsLarge && assignedPlot != null && assignedPlot.IsLargePlotType();
            var diameter = (slot.IsLarge || isSpanningLarge) ? SlotDiameterLarge : SlotDiameterSmall;
            var (renderX, renderY) = isSpanningLarge
                ? _currentLayout.GetSpanningSlotCenter(slot)
                : (slot.X, slot.Y);

            Brush fill;
            if (assignedPlot == null)
                fill = new SolidColorBrush(Color.FromArgb(120, 200, 200, 200));
            else
            {
                var statusFill = StatusFillBrush(assignedPlot.SlotDots, assignedPlot.PlotSentState);
                fill = statusFill ?? PlotTypeBrush(assignedPlot.PlotType);
            }

            var ellipse = new Ellipse
            {
                Width = diameter,
                Height = diameter,
                Fill = fill,
                Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                StrokeThickness = 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ellipse, renderX - diameter / 2);
            Canvas.SetTop(ellipse, renderY - diameter / 2);
            overlayCanvas.Children.Add(ellipse);

            if (!isSpanningLarge)
            {
                var label = new TextBlock
                {
                    Text = IslandLayouts.FormatSlotLabel(slot.SlotIndex),
                    FontSize = 8,
                    Foreground = Brushes.LightGray,
                    Width = 60,
                    TextAlignment = TextAlignment.Center,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(label, renderX - 30);
                Canvas.SetTop(label, renderY + diameter / 2 + 2);
                overlayCanvas.Children.Add(label);
            }
        }
    }

    // ── Slot canvas ───────────────────────────────────────────────────────────

    private void RebuildSlots(MainWindowViewModel vm)
    {
        SlotCanvas.Children.Clear();

        if (_currentLayout == null) return;

        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        var domainIsland = _currentIsland != null ? controller?.GetById(_currentIsland.IslandId) : null;
        var plots = domainIsland?.Plots?.ToList() ?? new List<Island.IslandPlot>();

        foreach (var slot in _currentLayout.Slots)
        {
            var assignedPlot = plots.FirstOrDefault(p => p.MapSlotIndex == slot.SlotIndex);
            var userLabel = domainIsland?.GetSlotLabel(slot.SlotIndex) ?? string.Empty;

            // Small slot consumed by a large-type plot on its pair: render as a dim consumed indicator,
            // skip labels — the active slot already shows the status light.
            if (!slot.IsLarge && assignedPlot == null && _currentLayout.IsSmallSlotConsumedByPair(slot.SlotIndex, plots))
            {
                var consumedEllipse = new System.Windows.Shapes.Ellipse
                {
                    Width = SlotDiameterSmall,
                    Height = SlotDiameterSmall,
                    Fill = new SolidColorBrush(Color.FromArgb(50, 200, 200, 200)),
                    Stroke = new SolidColorBrush(Color.FromArgb(80, 180, 180, 180)),
                    StrokeThickness = 1.0,
                    IsHitTestVisible = false,
                    ToolTip = $"Slot {IslandLayouts.FormatSlotLabel(slot.SlotIndex)}: {LocalizationController.Translation("ISLAND_MANAGEMENT_MAP_CONSUMED")}"
                };
                Canvas.SetLeft(consumedEllipse, slot.X - SlotDiameterSmall / 2);
                Canvas.SetTop(consumedEllipse, slot.Y - SlotDiameterSmall / 2);
                SlotCanvas.Children.Add(consumedEllipse);
                continue;
            }

            // Small slot with a large-type plot spanning both small slots:
            // render at midpoint between the pair at large size, with plot labels.
            var isSpanningLarge = !slot.IsLarge && assignedPlot != null && assignedPlot.IsLargePlotType();
            var diameter = (slot.IsLarge || isSpanningLarge) ? SlotDiameterLarge : SlotDiameterSmall;
            var (renderX, renderY) = isSpanningLarge
                ? _currentLayout.GetSpanningSlotCenter(slot)
                : (slot.X, slot.Y);

            var ellipse = BuildSlotEllipse(slot, assignedPlot, isSpanningLarge);
            ellipse.Tag = slot.SlotIndex;
            ellipse.MouseRightButtonUp += SlotEllipse_RightClick;
            ellipse.AllowDrop = true;
            ellipse.Drop += SlotEllipse_Drop;
            ellipse.DragOver += SlotEllipse_DragOver;

            Canvas.SetLeft(ellipse, renderX - diameter / 2);
            Canvas.SetTop(ellipse, renderY - diameter / 2);
            SlotCanvas.Children.Add(ellipse);

            // Spanning large plot: show plot labels at midpoint, suppress S1/S2 slot name.
            if (!isSpanningLarge)
            {
                var nameLabel = BuildSlotNameLabel(slot, userLabel, diameter);
                SlotCanvas.Children.Add(nameLabel);
            }

            if (assignedPlot != null)
            {
                var plotLabel = BuildPlotTypeLabel(slot, assignedPlot, diameter);
                if (isSpanningLarge)
                {
                    Canvas.SetLeft(plotLabel, renderX - 30);
                    Canvas.SetTop(plotLabel, renderY - diameter / 2 - 14);
                }
                SlotCanvas.Children.Add(plotLabel);
            }
        }
    }

    private static Ellipse BuildSlotEllipse(IslandSlotDefinition slot, Island.IslandPlot assignedPlot, bool isSpanningLarge = false)
    {
        var diameter = (slot.IsLarge || isSpanningLarge) ? SlotDiameterLarge : SlotDiameterSmall;

        Brush fill;
        if (assignedPlot == null)
            fill = new SolidColorBrush(Color.FromArgb(120, 200, 200, 200));
        else
        {
            var statusFill = StatusFillBrush(assignedPlot.SlotDots, assignedPlot.PlotSentState);
            fill = statusFill ?? PlotTypeBrush(assignedPlot.PlotType);
        }

        return new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = fill,
            Stroke = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            StrokeThickness = 1.0,
            Cursor = Cursors.Hand,
            ToolTip = assignedPlot != null
                ? $"Slot {IslandLayouts.FormatSlotLabel(slot.SlotIndex)}: {assignedPlot.BuildingTypeName}"
                : $"Slot {IslandLayouts.FormatSlotLabel(slot.SlotIndex)}: {LocalizationController.Translation("ISLAND_MANAGEMENT_MAP_UNASSIGNED")}"
        };
    }

    private static Brush StatusFillBrush(IReadOnlyList<string> dots, string plotSentState = null)
    {
        // plotSentState is the authoritative aggregate — use it when available
        if (!string.IsNullOrEmpty(plotSentState))
        {
            if (plotSentState == "loot_ready") return new SolidColorBrush(Color.FromRgb(255, 190, 50));
            if (plotSentState == "on_job" || plotSentState == "sent") return new SolidColorBrush(Color.FromRgb(80, 220, 80));
            return null;
        }
        // Fallback for non-house plots without plotSentState (farmables use dots directly)
        if (dots == null || dots.Count == 0 || dots.All(d => d == "none"))
            return null;
        if (dots.Any(d => d == "loot_ready"))
            return new SolidColorBrush(Color.FromRgb(255, 190, 50));
        if (dots.Any(d => d == "on_job") || dots.Any(d => d == "sent"))
            return new SolidColorBrush(Color.FromRgb(80, 220, 80));
        return null;
    }

    private UIElement BuildSlotNameLabel(IslandSlotDefinition slot, string userLabel, double diameter)
    {
        var tb = new TextBlock
        {
            Text = string.IsNullOrEmpty(userLabel) ? IslandLayouts.FormatSlotLabel(slot.SlotIndex) : userLabel,
            FontSize = 8,
            Foreground = string.IsNullOrEmpty(userLabel) ? Brushes.LightGray : Brushes.White,
            Width = 60,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = true,
            Cursor = Cursors.IBeam,
            Tag = slot.SlotIndex
        };

        tb.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                BeginSlotLabelEdit(slot.SlotIndex, (UIElement) s, userLabel);
            }
        };

        Canvas.SetLeft(tb, slot.X - 30);
        Canvas.SetTop(tb, slot.Y + diameter / 2 + 2);
        return tb;
    }

    private void BeginSlotLabelEdit(int slotIndex, UIElement labelElement, string currentLabel)
    {
        var pos = labelElement.TranslatePoint(new Point(0, 0), SlotCanvas);

        var editor = new TextBox
        {
            Text = currentLabel,
            FontSize = 7,
            Width = 60,
            Height = 14,
            Padding = new Thickness(1),
            Tag = slotIndex
        };

        Canvas.SetLeft(editor, pos.X);
        Canvas.SetTop(editor, pos.Y);
        SlotCanvas.Children.Add(editor);
        editor.Focus();
        editor.SelectAll();

        editor.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitSlotLabel(slotIndex, editor.Text);
                SlotCanvas.Children.Remove(editor);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                SlotCanvas.Children.Remove(editor);
                e.Handled = true;
            }
        };

        editor.LostFocus += (s, e) =>
        {
            if (SlotCanvas.Children.Contains(editor))
            {
                CommitSlotLabel(slotIndex, editor.Text);
                SlotCanvas.Children.Remove(editor);
            }
        };
    }

    private void CommitSlotLabel(int slotIndex, string text)
    {
        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        if (controller == null || _currentIsland == null) return;

        var island = controller.GetById(_currentIsland.IslandId);
        if (island == null) return;

        island.SetSlotLabel(slotIndex, text);
        controller.UpdateIsland(island);

        if (DataContext is MainWindowViewModel vm)
            RebuildSlots(vm);
    }

    private static TextBlock BuildPlotTypeLabel(IslandSlotDefinition slot, Island.IslandPlot plot, double diameter)
    {
        return new TextBlock
        {
            Text = plot.BuildingTypeName.Length > 5 ? plot.BuildingTypeName[..5] : plot.BuildingTypeName,
            FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 230, 120)),
            Width = 60,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        }.Also(tb =>
        {
            Canvas.SetLeft(tb, slot.X - 30);
            Canvas.SetTop(tb, slot.Y + diameter / 2 + 11);
        });
    }

    // ── Unassigned chips panel ────────────────────────────────────────────────

    private void RebuildUnassignedChips()
    {
        UnassignedPlotsList.ItemsSource = null;

        if (_currentIsland == null)
        {
            UnassignedPlotsList.ItemsSource = Array.Empty<PlotChipItem>();
            return;
        }

        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        var domainIsland = controller?.GetById(_currentIsland.IslandId);
        if (domainIsland == null)
        {
            UnassignedPlotsList.ItemsSource = Array.Empty<PlotChipItem>();
            return;
        }

        var chips = domainIsland.Plots
            .Where(p => p.MapSlotIndex == null)
            .Select(p => new PlotChipItem(p.Id, $"#{p.PlotNumber} {p.PlotType.GetDisplayName()}", PlotTypeBrush(p.PlotType)))
            .ToList();

        UnassignedPlotsList.ItemsSource = chips;
    }

    private void PlotChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: PlotChipItem chip }) return;
        DragDrop.DoDragDrop((DependencyObject) sender, new DataObject(DragPlotFormat, chip.PlotId), DragDropEffects.Move);
    }

    // ── Drag-and-drop on map ──────────────────────────────────────────────────

    private static bool HasPlotDragData(DragEventArgs e) =>
        e.Data.GetDataPresent(DragPlotFormat);

    private void MapGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasPlotDragData(e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void MapGrid_Drop(object sender, DragEventArgs e)
    {
        // Drop on canvas background — no slot targeted
        e.Handled = true;
    }

    private void SlotEllipse_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasPlotDragData(e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void SlotEllipse_Drop(object sender, DragEventArgs e)
    {
        if (!HasPlotDragData(e)) return;
        if (sender is not Ellipse { Tag: int slotIndex }) return;
        if (e.Data.GetData(DragPlotFormat) is not Guid plotId) return;

        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        if (controller == null || _currentIsland == null) return;

        var island = controller.GetById(_currentIsland.IslandId);
        if (island == null) return;

        // Unassign whatever was in this slot before
        var prev = island.Plots.FirstOrDefault(p => p.MapSlotIndex == slotIndex);
        if (prev != null) prev.MapSlotIndex = null;

        // Assign the dragged plot
        var target = island.Plots.FirstOrDefault(p => p.Id == plotId);
        if (target != null) target.MapSlotIndex = slotIndex;

        controller.UpdateIsland(island);
        if (DataContext is MainWindowViewModel vm)
        {
            RebuildSlots(vm);
            RebuildUnassignedChips();
        }

        e.Handled = true;
    }

    // ── Right-click context menu (clear / delete) ─────────────────────────────

    private void SlotEllipse_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainWindowViewModel) return;
        if (sender is not Ellipse { Tag: int slotIndex }) return;
        if (_currentIsland == null) return;

        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        if (controller == null) return;

        var island = controller.GetById(_currentIsland.IslandId);
        if (island == null) return;

        var assigned = island.Plots.FirstOrDefault(p => p.MapSlotIndex == slotIndex);
        var menu = new ContextMenu();

        if (assigned != null)
        {
            var clearItem = new MenuItem { Header = $"Clear assignment ({assigned.PlotType.GetDisplayName()})" };
            clearItem.Click += (_, _) =>
            {
                assigned.MapSlotIndex = null;
                controller.UpdateIsland(island);
                if (DataContext is MainWindowViewModel vm)
                {
                    RebuildSlots(vm);
                    RebuildUnassignedChips();
                }
            };
            menu.Items.Add(clearItem);

            var deleteItem = new MenuItem { Header = $"Delete plot (#{assigned.PlotNumber} {assigned.PlotType.GetDisplayName()})" };
            deleteItem.Click += (_, _) =>
            {
                island.RemovePlot(assigned);
                controller.UpdateIsland(island);
                if (DataContext is MainWindowViewModel vm)
                {
                    RebuildSlots(vm);
                    RebuildUnassignedChips();
                }
            };
            menu.Items.Add(deleteItem);
        }
        else
        {
            var empty = new MenuItem { Header = "Slot unassigned — drag a plot here", IsEnabled = false };
            menu.Items.Add(empty);
        }

        menu.PlacementTarget = (UIElement) sender;
        menu.IsOpen = true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Brush PlotTypeBrush(Island.PlotType plotType) => plotType switch
    {
        Island.PlotType.House => new SolidColorBrush(Color.FromRgb(100, 149, 237)),
        Island.PlotType.Farm => new SolidColorBrush(Color.FromRgb(144, 238, 144)),
        Island.PlotType.HerbGarden => new SolidColorBrush(Color.FromRgb(60, 179, 113)),
        Island.PlotType.Pasture => new SolidColorBrush(Color.FromRgb(245, 222, 179)),
        Island.PlotType.Kennel => new SolidColorBrush(Color.FromRgb(210, 180, 140)),
        _ => new SolidColorBrush(Color.FromRgb(180, 140, 210))
    };

    private sealed record PlotChipItem(Guid PlotId, string Label, Brush ChipBrush);
}

internal static class UIElementExtensions
{
    internal static T Also<T>(this T self, Action<T> configure) where T : UIElement
    {
        configure(self);
        return self;
    }
}
