using Serilog;
using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.GameFileData;
using StatisticsAnalysisTool.Common.UserSettings;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Models.BindingModel;
using StatisticsAnalysisTool.Models.NetworkModel;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Operations.Responses;
using StatisticsAnalysisTool.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace StatisticsAnalysisTool.Network.Manager;

// Domain island list CRUD and JSON persistence for IslandController.
public partial class IslandController
{
    public IReadOnlyList<Island.Island> Islands
    {
        get { lock (_islandsLock) return _islands.ToList(); }
    }

    public Guid? AddIsland(Island.Island island)
    {
        ArgumentNullException.ThrowIfNull(island);
        lock (_islandsLock)
        {
            var isDuplicate = _islands.Any(i =>
                string.Equals(i.Name, island.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.City, island.City, StringComparison.OrdinalIgnoreCase));
            if (isDuplicate)
            {
                Log.Warning("[IslandController] Duplicate island rejected: name={Name} city={City}", island.Name, island.City);
                return null;
            }
            _islands.Add(island);
        }

        RefreshBindingsAsync();
        _ = SaveToFileAsync();
        return island.Id;
    }

    public void SelectIslandById(Guid id)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var bindings = _mainWindowViewModel?.IslandBindings;
            if (bindings == null) return;
            var entry = bindings.Islands?.FirstOrDefault(e => e.IslandId == id);
            if (entry != null)
                bindings.SelectedIsland = entry;
        });
    }

    public bool IslandExists(string name, string city)
    {
        lock (_islandsLock)
            return _islands.Any(i =>
                string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(i.City, city, StringComparison.OrdinalIgnoreCase));
    }

    public void UpdateIsland(Island.Island island)
    {
        ArgumentNullException.ThrowIfNull(island);
        lock (_islandsLock)
        {
            var idx = _islands.FindIndex(x => x.Id == island.Id);
            if (idx >= 0)
                _islands[idx] = island;
        }

        RefreshBindingsAsync();
        _ = SaveToFileAsync();
    }

    public void RemoveIsland(Guid id)
    {
        lock (_islandsLock)
            _islands.RemoveAll(x => x.Id == id);

        RefreshBindingsAsync();
        _ = SaveToFileAsync();
    }

    public Island.Island GetById(Guid id)
    {
        lock (_islandsLock)
            return _islands.FirstOrDefault(x => x.Id == id);
    }

    public async Task LoadFromFileAsync()
    {
        var (islands, migrated) = await IslandStore.LoadAsync();

        lock (_islandsLock)
        {
            _islands.Clear();
            _islands.AddRange(islands);
        }

        if (migrated)
            await SaveToFileAsync();

        await LoadOwnerProfilesAsync();

        RefreshBindingsAsync();
        Log.Information("[IslandController] Loaded {Count} islands from file.", islands.Count);
    }

    public async Task SaveToFileAsync()
    {
        List<Island.Island> snapshot;
        lock (_islandsLock)
            snapshot = _islands.ToList();

        await IslandStore.SaveAsync(snapshot);
    }
}
