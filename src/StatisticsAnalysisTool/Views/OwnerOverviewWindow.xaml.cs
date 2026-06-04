using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.ViewModels;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace StatisticsAnalysisTool.Views;

public partial class OwnerOverviewWindow : Window
{
    private bool _suppressOwnerSync;

    public OwnerOverviewWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.IslandBindings.PropertyChanged += OnIslandBindingsPropertyChanged;
        Closed += (_, _) => viewModel.IslandBindings.PropertyChanged -= OnIslandBindingsPropertyChanged;
    }

    private void OnIslandBindingsPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.BindingModel.IslandBindings.SelectedOverviewOwner))
        {
            SyncOwnerListBoxSelection();
        }
    }

    private void SyncOwnerListBoxSelection()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var owner = vm.IslandBindings.SelectedOverviewOwner;
        _suppressOwnerSync = true;
        try
        {
            if (OwnerListBox.SelectionMode == SelectionMode.Single)
            {
                OwnerListBox.SelectedItem = string.IsNullOrEmpty(owner) ? null : (object)owner;
            }
            else
            {
                var parts = owner?.Split(new[] { '|', ',' }, System.StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim()).ToHashSet(System.StringComparer.OrdinalIgnoreCase)
                            ?? [];
                foreach (var ownerName in OwnerListBox.Items.OfType<string>())
                {
                    if (OwnerListBox.ItemContainerGenerator.ContainerFromItem(ownerName) is ListBoxItem item)
                        item.IsSelected = parts.Contains(ownerName);
                }
            }
        }
        finally
        {
            _suppressOwnerSync = false;
        }
    }

    private void OwnerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOwnerSync) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not ListBox lb) return;
        var selected = lb.SelectedItems.Cast<string>().ToList();
        vm.IslandBindings.SelectedOverviewOwner = string.Join("|", selected);
    }

    private void BtnQuickFillTodayCycles_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.QuickFillTodayCycles();
    }

    private void BtnRecordCycle_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.IslandBindings.TryRecordCycle(out var error) && !string.IsNullOrEmpty(error))
            MessageBox.Show(error, "Record Cycle", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BtnRecordWithdrawal_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.IslandBindings.TryRecordWithdrawal(out var error) && !string.IsNullOrEmpty(error))
            MessageBox.Show(error, "Record Payout", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void EditLedgerEntry_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (sender is not Button { Tag: OwnerLedgerEntry entry }) return;

        if (entry.IsEarning)
        {
            var record = vm.IslandBindings.GetCycleRecord(entry.Id);
            if (record == null) return;
            var dialog = new EditCycleRecordWindow(record) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (dialog.DeleteRequested)
                vm.IslandBindings.DeleteLedgerEntry(entry.Id, entry.IsEarning);
            else if (dialog.Result != null)
                vm.IslandBindings.UpdateLedgerCycleEntry(dialog.Result);
        }
        else
        {
            var withdrawal = vm.IslandBindings.GetWithdrawalRecord(entry.Id);
            if (withdrawal == null) return;
            var dialog = new EditCycleRecordWindow(withdrawal) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (dialog.DeleteRequested)
                vm.IslandBindings.DeleteLedgerEntry(entry.Id, entry.IsEarning);
            else if (dialog.WithdrawalResult != null)
                vm.IslandBindings.UpdateLedgerWithdrawalEntry(dialog.WithdrawalResult);
        }
    }


    private void BtnLedgerPrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.FinanceHistoryPrevPage();
    }

    private void BtnLedgerNextPage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.FinanceHistoryNextPage();
    }

    private async void BtnSendWebhook_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var bindings = vm.IslandBindings;
        var ownerName = bindings.EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(ownerName)) return;

        if (!bindings.AllIslandsDoneToday)
        {
            var confirm = new DialogWindow(
                "Send Webhook",
                "Not all islands are done today. Send anyway?",
                Enumerations.DialogType.YesNo)
            {
                Owner = this
            };
            if (confirm.ShowDialog() != true || confirm.DialogWindowViewModel.Canceled)
                return;
        }

        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        if (controller == null) return;

        var sent = await controller.SendWebhookManualAsync(ownerName);
        if (!sent)
            MessageBox.Show("Webhook not configured or message empty.", "Send Webhook", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BtnCopyMessage_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var message = vm.IslandBindings.BuildDiscordMessage();
        if (string.IsNullOrEmpty(message)) return;
        Clipboard.SetText(message);
    }

    private void BtnImportCode_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var dialog = new ImportCodeWindow { Owner = this };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.EnteredCode))
            return;

        if (!vm.IslandBindings.TryImportCode(dialog.EnteredCode, out var error))
            MessageBox.Show(error, "Import Code", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ToggleOwnerProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.Preferences.ShowOwnerManagementSection = !vm.IslandBindings.Preferences.ShowOwnerManagementSection;
    }

    private void ToggleFinanceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.Preferences.ShowFinanceSettingsSection = !vm.IslandBindings.Preferences.ShowFinanceSettingsSection;
    }

    private void ToggleFinanceHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.Preferences.ShowFinanceHistorySection = !vm.IslandBindings.Preferences.ShowFinanceHistorySection;
    }

    private void ToggleDiscordAlerts_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.Preferences.ShowDiscordWebhookSection = !vm.IslandBindings.Preferences.ShowDiscordWebhookSection;
    }

    private void ToggleYieldSummary_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.Preferences.IsYieldSummarySectionCollapsed = !vm.IslandBindings.Preferences.IsYieldSummarySectionCollapsed;
    }

    private void ToggleIslandSummary_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.Preferences.IsIslandSummarySectionCollapsed = !vm.IslandBindings.Preferences.IsIslandSummarySectionCollapsed;
    }

    private void ToggleFinanceRecord_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.Preferences.IsFinanceRecordSectionCollapsed = !vm.IslandBindings.Preferences.IsFinanceRecordSectionCollapsed;
    }

    private void BtnYieldViewByItem_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.IslandYieldViewMode = 0;
    }

    private void BtnYieldViewByIsland_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.IslandYieldViewMode = 1;
    }

    private void BtnClearAllOwnerYield_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.ClearAllOwnerYield();
    }

    private void BtnCopyAllHistory_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var ledger = vm.IslandBindings.SelectedOwnerLedger;
        if (ledger.Count == 0) return;
        Clipboard.SetText(BuildLedgerText(ledger));
    }

    private void BtnCopyCurrentPeriod_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var bindings = vm.IslandBindings;
        var periodStart = bindings.SelectedOwnerCurrentPeriodStartDate;
        var periodEnd = bindings.SelectedOwnerNextPayoutDate;
        var entries = bindings.SelectedOwnerLedger
            .Where(entry => entry.Date.Date >= periodStart && entry.Date.Date <= periodEnd)
            .ToList();
        if (entries.Count == 0) return;
        Clipboard.SetText(BuildLedgerText(entries));
    }

    private static string BuildLedgerText(IEnumerable<OwnerLedgerEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"Date",-12}| {"Type",-8}| {"Islands",-8}| {"Amount",14} | Notes");
        sb.AppendLine(new string('-', 70));
        foreach (var e in entries)
        {
            var islands = e.IslandCount.HasValue ? e.IslandCount.Value.ToString() : string.Empty;
            sb.AppendLine($"{e.Date:yyyy-MM-dd}  | {e.Type,-8}| {islands,-8}| {e.Amount,14:N0} | {e.Notes}");
        }
        return sb.ToString();
    }

    private void OwnerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IslandBindings.Preferences.IsOwnerOverviewSettingsOpen = !vm.IslandBindings.Preferences.IsOwnerOverviewSettingsOpen;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
