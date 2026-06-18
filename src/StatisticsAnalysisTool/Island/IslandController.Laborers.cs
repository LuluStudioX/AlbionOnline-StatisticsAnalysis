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

// Laborer object/job info handling and laborer snapshot projection for IslandController.
public partial class IslandController
{
    public void HandleLaborerObjectInfo(LaborerObjectInfoEvent e)
    {
        if (e.ObjectId < 0) return;
        if (!_snapshots.TryGetValue(e.ObjectId, out var snapshot))
        {
            // LaborerObjectInfo can arrive before NewBuilding on re-entry (new ObjectId after respawn).
            // Create a stub snapshot so names and job state are captured immediately.
            snapshot = _snapshots.GetOrAdd(e.ObjectId, id =>
            {
                var s = new LaborerSnapshot(id) { DetectionOrder = Interlocked.Increment(ref _detectionCounter) };
                lock (_snapshotOrderLock) _snapshotsByOrder.Add(s);
                return s;
            });
        }
        var wasOnJob = snapshot.IsOnJob;
        snapshot.UpdateFromLaborerObjectInfo(e);

        PushLiveStatusToBindings();

        var currentIsland = FindCurrentIsland();

        // Ensure tier/name updates from reconnect visits are reflected in config.
        // NewBuilding fires only on first detection in a session; LaborerObjectInfo fires every visit.
        if (currentIsland != null && snapshot.BuildingTier > 0 && !string.IsNullOrWhiteSpace(snapshot.LaborerType))
            TryEnsureHousePlotConfiguration(currentIsland, snapshot);

        if (e.IsOnJob && !wasOnJob && currentIsland != null)
        {
            currentIsland.TotalLaborersSent++;
            currentIsland.UpdateModificationDate();
            RequestSaveToFile();
            RefreshIslandStatusAsync(currentIsland);
        }

        TryAutoStartIslandTimerFromLaborer(snapshot);

        // Re-attempt on each on-job transition so the (gated) dialog opens after the LAST laborer, not the first.
        if (e.IsOnJob && !wasOnJob)
            TryShowPaymentReadyDialog(currentIsland?.Owner);

        LaborerSnapshotsChanged?.Invoke();
    }

    private void TryAutoStartIslandTimerFromLaborer(LaborerSnapshot snapshot)
    {
        var prefs = _mainWindowViewModel?.IslandBindings?.Preferences;
        if (prefs == null || !prefs.AutoStartCycleOnIslandActivity) return;
        if (!snapshot.IsOnJob) return;

        // Ready-at = param 8 (same-session dispatch); on reconnect param 8 is absent, so ReadyAtUtc
        // falls back to JobStartTime + base cycle. Param 6/7 are food timestamps and never used here.
        DateTime? readyUtcNullable = snapshot.ReadyAtUtc;

        if (!readyUtcNullable.HasValue) return;

        var island = FindCurrentIsland();
        if (island == null) return;

        var hasConfiguredHousePlots = island.Plots?.Any(p => p.PlotType == PlotType.House) == true;
        var houseMatchedOrAutofilled = TryEnsureHousePlotConfiguration(island, snapshot);
        if (hasConfiguredHousePlots && !houseMatchedOrAutofilled)
        {
            Log.Debug("[IslandController] Auto-start skipped: no matching house config for laborer {Laborer}", snapshot.FullName);
            return;
        }

        var readyUtc = readyUtcNullable.Value.ToUniversalTime();
        var cycleStartUtc = readyUtc.AddHours(-IslandConstants.LaborerBaseCycleHours);

        // Only a laborer genuinely OUT on a fresh cycle (return still in the future) may drive the island's
        // collection timer. A back/loot-ready laborer's anchor is already in the past; letting it set
        // LastCycleStartAt would make the island "first ready" show that laborer's (often shortest) time
        // instead of the real pending cycles — the island must not consider an already-ready laborer here.
        if (readyUtc <= DateTime.UtcNow) return;

        var shouldUpdate = !island.LastCycleStartAt.HasValue
            || island.LastCycleStartAt.Value.AddHours(IslandConstants.LaborerBaseCycleHours) <= DateTime.UtcNow;
        if (!shouldUpdate) return;

        island.LastCycleStartAt = cycleStartUtc;
        island.LastHandledAt = DateTime.UtcNow;
        island.UpdateModificationDate();
        RequestSaveToFile();
        RefreshIslandStatusAsync(island);
        TryAutoPrefillPayout(island);
        Log.Information("[IslandController] Auto-started island timer from laborer cycle: island={Island}, laborer={Laborer}, ready={ReadyUtc:O}, cycleStart={CycleStartUtc:O}",
            island.Name, snapshot.FullName, readyUtc, cycleStartUtc);
    }

    public void HandleLaborerObjectJobInfo(LaborerObjectJobInfoEvent e)
    {
        if (e.ObjectId < 0) return;
        if (!_snapshots.TryGetValue(e.ObjectId, out var snapshot)) return;
        var wasOnJob = snapshot.IsOnJob;
        var prevJobStartTime = snapshot.JobStartTime;
        snapshot.UpdateFromJobInfo(e);

        // Yield is recorded from NewLaborerItem (code 32) quantity growth — see HandleLaborerItemDetail.

        if (e.JournalItemId > 0)
        {
            var journalName = ItemController.GetItemUniqueNameByIndex(e.JournalItemId);
            snapshot.TrySetTypeFromJournal(journalName);
        }

        // Dispatch detection — two paths:
        // 1. Transition observed this session: was home (HasBeenSeenAsHome), now away on job.
        // 2. Re-dispatch across visits: job start time changed since last observation.
        var isNewDispatch = e.JobStartTime.HasValue && e.JournalItemId > 0
                            && (
                                (snapshot.HasBeenSeenAsHome && !wasOnJob && snapshot.IsOnJob)
                                || (prevJobStartTime != null && e.JobStartTime != prevJobStartTime)
                            );

        Log.Debug("[IslandController] LaborerJobInfo: objectId={ObjId}, journalId={JournalId}, jobStart={JobStart}, prevJobStart={PrevJobStart}, awayOnJob={AwayOnJob}, isNewDispatch={IsNewDispatch}",
            e.ObjectId, e.JournalItemId, e.JobStartTime, prevJobStartTime, e.IsAwayOnJob, isNewDispatch);

        // Consumed/collected journals are tracked from the actual NewJournalItem (code 35) stack deltas
        // in HandleLaborerJournalDetail — NOT booked here per dispatch (that under-counted to one each).

        if (e.IsAwayOnJob)
            TryAutoStartIslandTimerFromLaborer(snapshot);

        UpdateLastSnapshotCache();
        PushLiveStatusToBindings();
        LaborerSnapshotsChanged?.Invoke();

        if (isNewDispatch)
        {
            TryTriggerCollectionReadyWebhook();
            TryShowPaymentReadyDialog(FindCurrentIsland()?.Owner);
        }
    }

    public IReadOnlyList<LaborerSnapshot> GetCurrentSnapshots()
    {
        List<LaborerSnapshot> current;
        lock (_snapshotOrderLock)
            current = _snapshotsByOrder.Count > 0 ? new List<LaborerSnapshot>(_snapshotsByOrder) : null;

        if (current != null) return current;

        var island = FindCurrentIsland();
        if (island == null) return Array.Empty<LaborerSnapshot>();

        lock (_lastSnapshotLock)
        {
            if (_lastSnapshotIslandId != Guid.Empty
                && _lastSnapshotIslandId == island.Id
                && (DateTime.UtcNow - _lastSnapshotUtc) <= TimeSpan.FromMinutes(5)
                && _lastSnapshotList.Count > 0)
            {
                return new List<LaborerSnapshot>(_lastSnapshotList);
            }
        }

        return Array.Empty<LaborerSnapshot>();
    }

    private void UpdateLastSnapshotCache()
    {
        var island = FindCurrentIsland();
        if (island == null) return;

        lock (_lastSnapshotLock)
        {
            _lastSnapshotIslandId = island.Id;
            _lastSnapshotUtc = DateTime.UtcNow;
            lock (_snapshotOrderLock)
                _lastSnapshotList = new List<LaborerSnapshot>(_snapshotsByOrder);
        }
    }
}
