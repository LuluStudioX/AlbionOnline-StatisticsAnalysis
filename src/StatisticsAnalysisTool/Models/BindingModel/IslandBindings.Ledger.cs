using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network.Manager;
using System.Collections.ObjectModel;

namespace StatisticsAnalysisTool.Models.BindingModel;

public partial class IslandBindings
{
    // ── Ledger ────────────────────────────────────────────────────────────────

    private ObservableCollection<OwnerLedgerEntry> _currentIslandLedger = new();
    public ObservableCollection<OwnerLedgerEntry> CurrentIslandLedger
    {
        get => _currentIslandLedger;
        private set
        {
            _currentIslandLedger = value;
            OnPropertyChanged();
        }
    }

    public void RefreshCurrentIslandLedger()
    {
        _currentIslandLedger.Clear();
        if (SelectedIsland == null) return;

        var controller = ServiceLocator.Resolve<TrackingController>()?.IslandController;
        if (controller == null) return;

        var entries = controller.GetLedgerForIsland(SelectedIsland.IslandId);
        foreach (var entry in entries)
            _currentIslandLedger.Add(entry);
    }
}
