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

// Laborer loot/journal yield tracking (collect-window correlation) and yield clearing for IslandController.
public partial class IslandController
{
    // True only for the resource families a laborer produces (raw + refined). Farm/herb/pasture products
    // also land in island storage and broadcast code 32, but they are tracked precisely by the harvest-
    // response path (HerbGarden/Pasture). Without this filter every farm item was double-recorded under
    // PlotType.House (e.g. T8_YARROW counted twice). Journals go through HandleLaborerJournalDetail.
    private static bool IsLaborerLootResource(string uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName)) return false;
        var u = uniqueName.ToUpperInvariant();
        if (u.Contains("FARM") || u.Contains("SEED")) return false;
        foreach (var token in LaborerResourceTokens)
            if (u.Contains(token)) return true;
        return false;
    }

    // Open the collect window: a laborer collect REQUEST (op 257) just fired, so the storage-stack growth
    // that follows over the next few seconds is real collected loot. Called from LaborerCollectRequestHandler.
    public void NotifyLaborerCollect(long laborerObjectId)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        System.Threading.Volatile.Write(ref _lastLaborerCollectTicks, nowTicks);
        FlushPendingYield(nowTicks);
        Log.Debug("[IslandController] Laborer collect request: objectId={ObjectId} — yield window opened", laborerObjectId);
    }

    // Growth seen outside the forward window is held here until a 257 confirms it (look-back). Trims stale
    // entries on each add so an idle period (no collect) can't let the buffer grow unbounded.
    private void BufferPendingYield(int itemIndex, int quantity)
    {
        if (quantity <= 0) return;
        var nowTicks = DateTime.UtcNow.Ticks;
        var cutoff = nowTicks - LaborerCollectLookback.Ticks;
        lock (_pendingYieldLock)
        {
            _pendingYield.RemoveAll(p => p.Ticks < cutoff);
            _pendingYield.Add(new PendingYield(nowTicks, itemIndex, quantity));
        }
    }

    // A 257 just fired — commit buffered growth from the look-back window (real loot that streamed in just
    // before the request) and drop the rest (uncorrelated repaints/streaming).
    private void FlushPendingYield(long collectTicks)
    {
        var cutoff = collectTicks - LaborerCollectLookback.Ticks;
        List<PendingYield> toCommit;
        lock (_pendingYieldLock)
        {
            toCommit = _pendingYield.FindAll(p => p.Ticks >= cutoff);
            _pendingYield.Clear();
        }

        foreach (var pending in toCommit)
            RecordCollectedYield(pending.ItemIndex, pending.Quantity);
    }

    // Book collected laborer yield (resource or empty journal) against the current island.
    private void RecordCollectedYield(int itemIndex, int quantity)
    {
        if (quantity <= 0) return;
        var island = FindCurrentIsland();
        if (island == null) return;

        island.AddYield(itemIndex, quantity, PlotType.House);
        island.TotalLootCollected += quantity;
        island.UpdateModificationDate();
        // Collecting fires this many times per second as each stack grows. Debounce the file save and
        // UI push so we don't flood the disk and dispatcher (which was starving the yield/card refresh).
        _yieldTracker.Schedule(island);

        Log.Information("[IslandController] Recorded collected laborer yield: island={Island}, itemId={ItemId}, qty={Qty}",
            island.Name, itemIndex, quantity);
    }

    // True while within LaborerCollectYieldWindow of the last collect request. Storage stacks (code 32/35)
    // are repainted/streamed/object-id-reused constantly; only growth inside this window is a real collect.
    private bool InLaborerCollectWindow()
    {
        var last = System.Threading.Volatile.Read(ref _lastLaborerCollectTicks);
        if (last == 0) return false;
        return DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) <= LaborerCollectYieldWindow;
    }

    public void HandleLaborerItemDetail(DiscoveredItem item)
    {
        if (item == null || item.ObjectId < 0 || item.ItemIndex <= 0 || item.Quantity <= 0) return;

        // Only laborer-produced resources count here — farm products are handled by the harvest path.
        if (!IsLaborerLootResource(ItemController.GetItemUniqueNameByIndex(item.ItemIndex))) return;

        // Yield = positive growth of a PERSISTENT island-storage stack only. Opening a laborer spawns
        // short-lived preview objects (new high object ids, destroyed by a code-27 on collect); those
        // appear exactly once, so a baseline-only rule never counts them — which is what keeps merely
        // viewing a laborer from inflating yield. Real storage stacks carry an entry-load baseline and
        // grow as loot is deposited, so only their growth is counted.
        var hadPrev = _lastItemQty.TryGetValue(item.ObjectId, out var prevQty);
        _lastItemQty[item.ObjectId] = item.Quantity;
        if (!hadPrev) return; // first sighting — baseline only (covers preview objects and pre-existing stock)

        var delta = item.Quantity - prevQty;
        if (delta <= 0) return; // no growth (or a stack rollover) — nothing collected

        // Count growth correlated with a real collect request. Inside the forward window book it now;
        // otherwise hold it in the look-back buffer — a 257 arriving within LaborerCollectLookback will
        // commit it (most collect growth lands just BEFORE the request). Uncorrelated growth (storage
        // repaint / zone-in stream / object-id reuse) ages out of the buffer uncounted.
        if (InLaborerCollectWindow())
            RecordCollectedYield(item.ItemIndex, delta);
        else
            BufferPendingYield(item.ItemIndex, delta);
    }

    // NewJournalItem (code 35) broadcasts a laborer-journal stack's CURRENT quantity. EMPTY journals
    // (…_JOURNAL_…_EMPTY) rise as laborers hand them back = collected; FULL journals (…_FULL) fall as
    // they are fed back in as fame fuel = consumed. Same baseline rule as resources (see above).
    public void HandleLaborerJournalDetail(DiscoveredItem item)
    {
        if (item == null || item.ObjectId < 0 || item.ItemIndex <= 0 || item.Quantity < 0) return;

        var name = ItemController.GetItemUniqueNameByIndex(item.ItemIndex);
        if (string.IsNullOrEmpty(name) || name.IndexOf("JOURNAL", StringComparison.OrdinalIgnoreCase) < 0) return;
        var isEmpty = name.EndsWith("_EMPTY", StringComparison.OrdinalIgnoreCase);
        var isFull = name.EndsWith("_FULL", StringComparison.OrdinalIgnoreCase);
        if (!isEmpty && !isFull) return;

        var hadPrev = _lastJournalQty.TryGetValue(item.ObjectId, out var prevQty);
        _lastJournalQty[item.ObjectId] = item.Quantity;

        var island = FindCurrentIsland();
        if (island == null) return;

        if (isEmpty)
        {
            // Baseline-only growth, same as resources: only a persistent stack's increase counts, so
            // preview/temp journal objects (seen once) never inflate the collected total.
            if (!hadPrev) return;
            var gained = item.Quantity - prevQty;
            if (gained <= 0) return;

            // Empty journals rise when laborers hand them back on collect. Same bidirectional correlation
            // as resources: book inside the forward window, otherwise hold for a 257 look-back commit so
            // growth arriving just before the request isn't dropped.
            if (InLaborerCollectWindow())
                RecordCollectedYield(item.ItemIndex, gained);
            else
                BufferPendingYield(item.ItemIndex, gained);

            return;
        }

        // full journal — consumed as it is spent
        if (!hadPrev) return; // need a baseline before a drop can be measured
        var spent = prevQty - item.Quantity;
        if (spent <= 0) return;

        island.AddConsumed(item.ItemIndex, spent, PlotType.House);
        island.UpdateModificationDate();
        _yieldTracker.Schedule(island);
        Log.Information("[IslandController] Recorded consumed journal: island={Island}, itemId={ItemId}, qty={Qty}, objectId={ObjectId}",
            island.Name, item.ItemIndex, spent, item.ObjectId);
    }

    public void ClearIslandYield(Guid islandId)
    {
        Island island;
        lock (_islandsLock)
            island = _islands.FirstOrDefault(i => i.Id == islandId);
        if (island == null) return;

        island.ClearYield();
        island.UpdateModificationDate();
        RequestSaveToFile();
        _yieldTracker.PushUpdate(island);
    }

    public void ClearAllYield(IEnumerable<Guid> islandIds)
    {
        List<Island> targets;
        lock (_islandsLock)
            targets = _islands.Where(i => islandIds.Contains(i.Id)).ToList();
        if (targets.Count == 0) return;

        foreach (var island in targets)
        {
            island.ClearYield();
            island.UpdateModificationDate();
            _yieldTracker.PushUpdate(island);
        }
        RequestSaveToFile();
    }
}
