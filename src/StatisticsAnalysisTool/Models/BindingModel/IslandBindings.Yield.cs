using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network.Manager;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace StatisticsAnalysisTool.Models.BindingModel;

public partial class IslandBindings
{
    private ObservableCollection<IslandYieldAggregateRow> _ownerYieldByItem = [];
    private ObservableCollection<IslandYieldAggregateRow> _ownerYieldByIsland = [];
    private int _islandYieldViewMode;

    public ObservableCollection<IslandYieldAggregateRow> OwnerYieldByItem
    {
        get => _ownerYieldByItem;
        private set { _ownerYieldByItem = value; OnPropertyChanged(); }
    }

    public ObservableCollection<IslandYieldAggregateRow> OwnerYieldByIsland
    {
        get => _ownerYieldByIsland;
        private set { _ownerYieldByIsland = value; OnPropertyChanged(); }
    }

    public double OwnerTotalYieldValue => _ownerYieldByItem.Sum(r => r.TotalAvgEstMarketValue);
    public double OwnerTotalConsumedValue => OwnerConsumedByItem.Sum(r => r.TotalAvgEstMarketValue);

    private ObservableCollection<IslandYieldAggregateRow> _ownerConsumedByItem = [];
    public ObservableCollection<IslandYieldAggregateRow> OwnerConsumedByItem
    {
        get => _ownerConsumedByItem;
        private set { _ownerConsumedByItem = value; OnPropertyChanged(); }
    }

    public int IslandYieldViewMode
    {
        get => _islandYieldViewMode;
        set { _islandYieldViewMode = value; OnPropertyChanged(); }
    }

    public void ClearIslandYield()
    {
        var islandId = SelectedIsland?.IslandId;
        if (islandId == null) return;
        ServiceLocator.Resolve<TrackingController>()?.IslandController?.ClearIslandYield(islandId.Value);
    }

    public void ResetSlotAssignments()
    {
        var islandId = SelectedIsland?.IslandId;
        if (islandId == null) return;
        ServiceLocator.Resolve<TrackingController>()?.IslandController?.ResetSlotAssignments(islandId.Value);
    }

    public void ClearAllOwnerYield()
    {
        var ids = GetOwnerIslands().Select(e => e.IslandId).ToList();
        if (ids.Count == 0) return;
        ServiceLocator.Resolve<TrackingController>()?.IslandController?.ClearAllYield(ids);
    }

    public void RefreshOwnerYield()
    {
        var ownerIslands = GetOwnerIslands();

        OwnerYieldByItem = BuildAggregateByItem(
            ownerIslands.SelectMany(e => e.YieldItems));

        OwnerYieldByIsland = BuildAggregateByIsland(
            ownerIslands, e => e.YieldItems);

        OwnerConsumedByItem = BuildAggregateByItem(
            ownerIslands.SelectMany(e => e.ConsumedItems));

        OnPropertyChanged(nameof(OwnerTotalYieldValue));
        OnPropertyChanged(nameof(OwnerTotalConsumedValue));
    }

    private List<IslandEntry> GetOwnerIslands()
    {
        var owner = SelectedOverviewOwner?.Trim();
        if (string.IsNullOrEmpty(owner))
            return Islands.ToList();

        var owners = owner.Split('|', System.StringSplitOptions.RemoveEmptyEntries)
                          .Select(o => o.Trim())
                          .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        return Islands.Where(e => owners.Contains(e.OwnerName?.Trim() ?? string.Empty)).ToList();
    }

    private static ObservableCollection<IslandYieldAggregateRow> BuildAggregateByItem(
        IEnumerable<IslandYieldEntry> entries)
    {
        var rows = entries
            .GroupBy(e => e.ItemIndex)
            .Select(g => new IslandYieldAggregateRow
            {
                ItemIndex = g.Key,
                TotalQuantity = g.Sum(e => e.Quantity)
            })
            .OrderByDescending(r => r.TotalAvgEstMarketValue)
            .ToList();

        return new ObservableCollection<IslandYieldAggregateRow>(rows);
    }

    private static ObservableCollection<IslandYieldAggregateRow> BuildAggregateByItem(
        IEnumerable<IslandConsumedEntry> entries)
    {
        var rows = entries
            .GroupBy(e => e.ItemIndex)
            .Select(g => new IslandYieldAggregateRow
            {
                ItemIndex = g.Key,
                TotalQuantity = g.Sum(e => e.Quantity)
            })
            .OrderByDescending(r => r.TotalAvgEstMarketValue)
            .ToList();

        return new ObservableCollection<IslandYieldAggregateRow>(rows);
    }

    private static ObservableCollection<IslandYieldAggregateRow> BuildAggregateByIsland(
        IEnumerable<IslandEntry> islands,
        System.Func<IslandEntry, IEnumerable<IslandYieldEntry>> selector)
    {
        var rows = new List<IslandYieldAggregateRow>();

        foreach (var island in islands)
        {
            var islandRows = selector(island)
                .GroupBy(e => e.ItemIndex)
                .Select(g => new IslandYieldAggregateRow
                {
                    ItemIndex = g.Key,
                    TotalQuantity = g.Sum(e => e.Quantity),
                    IslandName = island.Name
                })
                .OrderByDescending(r => r.TotalAvgEstMarketValue);

            rows.AddRange(islandRows);
        }

        return new ObservableCollection<IslandYieldAggregateRow>(rows);
    }
}
