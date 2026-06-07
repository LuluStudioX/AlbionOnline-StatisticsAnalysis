using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace StatisticsAnalysisTool.Network.Manager;

// Debounced yield/consumed UI + disk flush. Collecting fires the per-stack growth handlers many times
// per second; this coalesces them into one save + one in-place binding patch so the disk and dispatcher
// are not flooded (which previously starved the yield/card refresh). Owns only its debounce state — the
// controller passes the VM and a save callback.
public sealed class IslandYieldTracker
{
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly Func<Task> _saveAsync;

    private volatile Timer _yieldFlushTimer;
    private Island.Island _pendingYieldIsland;
    private readonly object _yieldFlushLock = new();

    public IslandYieldTracker(MainWindowViewModel mainWindowViewModel, Func<Task> saveAsync)
    {
        _mainWindowViewModel = mainWindowViewModel ?? throw new ArgumentNullException(nameof(mainWindowViewModel));
        _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
    }

    public void Schedule(Island.Island island)
    {
        lock (_yieldFlushLock)
        {
            _pendingYieldIsland = island;
            if (_yieldFlushTimer == null)
                _yieldFlushTimer = new Timer(_ => Flush(), null, 400, Timeout.Infinite);
            else
                _yieldFlushTimer.Change(400, Timeout.Infinite);
        }
    }

    // Flush any pending debounced yield immediately — used before a session reset commits the island
    // we're leaving, so its collected yield is not lost when the session state is cleared.
    public void FlushNow() => Flush();

    public void StopFlushTimer()
    {
        lock (_yieldFlushLock)
        {
            _yieldFlushTimer?.Dispose();
            _yieldFlushTimer = null;
        }
    }

    private void Flush()
    {
        Island.Island island;
        lock (_yieldFlushLock)
        {
            island = _pendingYieldIsland;
            _pendingYieldIsland = null;
        }
        if (island == null) return;
        _ = _saveAsync();
        PushUpdate(island);
    }

    // Push an island's yield/consumed rows to the UI without debouncing — callers that record a single
    // yield/consumed change outside the per-stack collect storm use this for an immediate refresh.
    public void PushUpdate(Island.Island island)
    {
        var bindings = _mainWindowViewModel?.IslandBindings;
        if (bindings == null) return;

        var mismatches = ComputeYieldMismatches(island);

        // Resolve the entry INSIDE the dispatcher: a binding rebuild can replace the entry instance
        // between now and the UI tick, so capturing it here would update an orphaned (off-screen)
        // entry while the visible SelectedIsland points at the new one. Looking it up on the UI thread
        // guarantees we update the live entry the Yield panel is bound to.
        Application.Current?.Dispatcher?.InvokeAsync(() =>
        {
            var entry = bindings.Islands.FirstOrDefault(e => e.IslandId == island.Id);
            if (entry == null) return;
            // Patch the collections in place (no wholesale replace) so the Yield panel updates the
            // changed rows only instead of clearing and rebuilding — kills the blank-flash on collect.
            entry.UpdateYieldItems(island.YieldHistory);
            entry.UpdateConsumedItems(island.ConsumedHistory);
            entry.SetYieldMismatches(mismatches);
            bindings.RefreshOwnerYield();
        });
    }

    private static IReadOnlyList<string> ComputeYieldMismatches(Island.Island island)
    {
        var mismatches = new List<string>();
        if (island.ConsumedHistory.Count == 0) return mismatches;

        var configuredJournalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plot in island.Plots?.Where(p => p.PlotType == PlotType.House) ?? Enumerable.Empty<IslandPlot>())
        {
            var cfg = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
            for (var slot = 1; slot <= 3; slot++)
            {
                if (cfg.TryGetValue(LaborerConfigHelper.JournalKey(slot), out var journal)
                    && !string.IsNullOrWhiteSpace(journal)
                    && !journal.Equals(LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                {
                    configuredJournalNames.Add(ItemController.GetCleanUniqueName(journal));
                }
            }
        }

        if (configuredJournalNames.Count == 0) return mismatches;

        foreach (var consumed in island.ConsumedHistory.Where(e => e.SourcePlot == PlotType.House))
        {
            var uniqueName = ItemController.GetUniqueNameByIndex(consumed.ItemIndex);
            var cleanName = ItemController.GetCleanUniqueName(uniqueName);
            if (!configuredJournalNames.Contains(cleanName))
            {
                var displayName = ItemController.GetItemByIndex(consumed.ItemIndex)?.LocalizedName ?? uniqueName;
                mismatches.Add($"{displayName} tracked but not in configured laborer slots");
            }
        }

        return mismatches;
    }
}
