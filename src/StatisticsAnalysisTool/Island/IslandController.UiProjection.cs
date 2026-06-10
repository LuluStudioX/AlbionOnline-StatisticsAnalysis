using Serilog;
using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.GameFileData;
using StatisticsAnalysisTool.Common.UserSettings;
using StatisticsAnalysisTool.Enumerations;
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

namespace StatisticsAnalysisTool.Island;

// Live-status projection to the island bindings (push/refresh) for IslandController.
public partial class IslandController
{
    private void PushLiveStatusToBindings()
    {
        if (_pushDebounceTimer != null)
        {
            _pushDebounceTimer.Change(PushDebounceMs, Timeout.Infinite);
            return;
        }
        var t = new System.Threading.Timer(_ => ExecutePushSessionIslandStatus(), null, PushDebounceMs, Timeout.Infinite);
        if (Interlocked.CompareExchange(ref _pushDebounceTimer, t, null) != null)
            t.Dispose();
    }

    private void ExecutePushSessionIslandStatus()
    {
        var snapshots = GetCurrentSnapshots();

        List<Island> islandsCopy;
        Guid? sessionIslandId;
        lock (_islandsLock)
        {
            var sessionIsland = FindCurrentIslandNoLock();
            sessionIslandId = sessionIsland?.Id;

            if (sessionIsland?.Plots != null)
            {
                var assignments = IslandLaborerResolver.Resolve(
                    sessionIsland.Plots.Where(p => p.PlotType == PlotType.House).ToList(), snapshots);
                HealHouseMapSlots(sessionIsland, assignments);
                var anyChanged = false;
                foreach (var p in sessionIsland.Plots)
                {
                    assignments.TryGetValue(p.Id, out var slotMap);
                    if (p.UpdateLaborerStatuses(snapshots, sessionIsland.LastPlantedAt, slotMap)) anyChanged = true;
                }
                if (anyChanged)
                {
                    sessionIsland.UpdateModificationDate();
                    RequestSaveToFile();
                }
            }

            islandsCopy = new List<Island>(_islands);
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _mainWindowViewModel?.IslandBindings?.UpdateLiveStatus(snapshots, islandsCopy, sessionIslandId);
            ScheduleNextPlotTransition();
        });
        LaborerSnapshotsChanged?.Invoke();
    }

    private void ExecutePushAllIslandsStatus()
    {
        var snapshots = GetCurrentSnapshots();

        List<Island> islandsCopy;
        Guid? sessionIslandId;
        lock (_islandsLock)
        {
            var sessionIsland = FindCurrentIslandNoLock();
            sessionIslandId = sessionIsland?.Id;

            foreach (var isl in _islands)
            {
                if (isl.Plots == null) continue;
                var islSnapshots = isl.Id == sessionIslandId ? snapshots : Array.Empty<LaborerSnapshot>();
                IReadOnlyDictionary<Guid, IReadOnlyDictionary<int, LaborerSnapshot>> assignments = null;
                if (islSnapshots.Count > 0)
                {
                    assignments = IslandLaborerResolver.Resolve(
                        isl.Plots.Where(p => p.PlotType == PlotType.House).ToList(), islSnapshots);
                    HealHouseMapSlots(isl, assignments);
                }
                var anyChanged = false;
                foreach (var p in isl.Plots)
                {
                    IReadOnlyDictionary<int, LaborerSnapshot> slotMap = null;
                    assignments?.TryGetValue(p.Id, out slotMap);
                    if (p.UpdateLaborerStatuses(islSnapshots, isl.LastPlantedAt, slotMap)) anyChanged = true;
                }
                if (anyChanged)
                {
                    isl.UpdateModificationDate();
                    RequestSaveToFile();
                }
            }

            islandsCopy = new List<Island>(_islands);
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var isl in islandsCopy)
                isl.RefreshTimerDisplay();
            _mainWindowViewModel?.IslandBindings?.UpdateLiveStatus(snapshots, islandsCopy, sessionIslandId);
            ScheduleNextPlotTransition();
        });
    }

    private void RefreshBindingsAsync()
    {
        List<IslandEntry> entries;
        List<Island> islandsCopy;
        var snapshots = GetCurrentSnapshots();
        Guid? sessionIslandId;
        lock (_islandsLock)
        {
            entries = _islands.Select((isl, i) => IslandMapping.ToEntry(isl, i)).ToList();
            islandsCopy = new List<Island>(_islands);
            sessionIslandId = FindCurrentIslandNoLock()?.Id;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var bindings = _mainWindowViewModel?.IslandBindings;
            if (bindings == null) return;
            bindings.LoadFrom(entries);
            bindings.UpdateLiveStatus(snapshots, islandsCopy, sessionIslandId);
        });
    }

    // Refreshes only one island's live status in the bindings — no collection rebuild.
    // Use this when only one island's data changed (timer, plot config, laborer state).
    private void RefreshIslandStatusAsync(Island island)
    {
        var snapshots = GetCurrentSnapshots();
        var islandSnapshot = island;
        Guid? sessionIslandId;
        lock (_islandsLock)
            sessionIslandId = FindCurrentIslandNoLock()?.Id;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _mainWindowViewModel?.IslandBindings?.UpdateLiveStatus(snapshots, [islandSnapshot], sessionIslandId);
        });
        LaborerSnapshotsChanged?.Invoke();
    }
}
