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

// Collection-ready webhook triggering and manual webhook sending for IslandController.
public partial class IslandController
{
    private void TryTriggerCollectionReadyWebhook()
    {
        if (Volatile.Read(ref _collectionReadyWebhookSentThisSession) != 0) return;
        var prefs = _mainWindowViewModel?.IslandBindings?.Preferences;
        if (prefs?.AutoNotifyOwnerWhenAllDone != true) return;

        var snapshots = _snapshots.Values.ToList();
        if (snapshots.Count == 0) return;
        // The island we're on must itself be fully re-dispatched (all laborers away on a fresh job).
        if (!snapshots.All(s => s.IsOnJob)) return;

        var islandOwner = FindCurrentIsland()?.Owner?.Trim() ?? _sessionOwner;
        if (string.IsNullOrWhiteSpace(islandOwner)) return;

        // Fire only when EVERY island of this owner is done this cycle — i.e. none still NeedsVisit
        // (ready/overdue, or never planted).
        List<Island> ownerIslands;
        lock (_islandsLock)
            ownerIslands = _islands
                .Where(i => string.Equals(i.Owner?.Trim(), islandOwner, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (ownerIslands.Count == 0) return;
        var pending = ownerIslands.Where(i => i.NeedsVisit).ToList();
        if (pending.Count > 0)
        {
            Log.Debug("[IslandController] Owner webhook held: {Owner} still has {Count} island(s) to collect: {Names}",
                islandOwner, pending.Count, string.Join(", ", pending.Select(i => i.Name)));
            return;
        }

        // Atomic claim: if another push thread already flipped the flag, abort so we send exactly once.
        if (Interlocked.CompareExchange(ref _collectionReadyWebhookSentThisSession, 1, 0) != 0) return;
        Log.Information("[IslandController] All {Count} islands done for owner {Owner} — triggering collection-ready webhook.",
            ownerIslands.Count, islandOwner);
        _ = TrySendCollectionReadyWebhookAsync(islandOwner);
    }

    private async Task TrySendCollectionReadyWebhookAsync(string ownerName)
    {
        if (string.IsNullOrEmpty(ownerName)) return;

        var profile = GetOwnerProfile(ownerName);
        if (string.IsNullOrWhiteSpace(profile?.WebhookUrl)) return;

        var outcome = await _webhookService.PromptAsync().ConfigureAwait(false);
        if (!outcome.Send) return;

        if (outcome.SaveNote)
            ApplyWebhookNote(ownerName, outcome.Notes, outcome.Emv);

        var message = _mainWindowViewModel?.IslandBindings?.BuildDiscordMessage(ownerName);
        if (string.IsNullOrEmpty(message)) return;

        Log.Information("[IslandController] Sending collection-ready webhook: owner={Owner}", ownerName);
        await _webhookService.SendAsync(profile.WebhookUrl, message).ConfigureAwait(false);
    }

    // Persist the daily notes / EMV captured by the "Save and send" path onto the owner's cycle history.
    private void ApplyWebhookNote(string ownerName, string notes, decimal? emv)
    {
        if (string.IsNullOrWhiteSpace(notes) && !emv.HasValue) return;

        lock (_ownerProfilesLock)
        {
            var profile = GetOwnerProfile(ownerName);
            if (profile != null)
            {
                var today = IslandTime.Today;
                var record = profile.CycleHistory?
                    .FirstOrDefault(c => c.Date.Date == today && c.RecordType == CycleRecordType.Islands);

                if (record != null && !string.IsNullOrWhiteSpace(notes))
                {
                    record.Notes = string.IsNullOrWhiteSpace(record.Notes) || string.Equals(record.Notes.Trim(), AutoPrefillNotesMarker, StringComparison.OrdinalIgnoreCase)
                        ? notes
                        : $"{record.Notes}; {notes}";
                }

                if (emv.HasValue)
                {
                    profile.CycleHistory.Add(new OwnerCycleRecord
                    {
                        Date = today,
                        RecordType = CycleRecordType.Other,
                        EarnedAmount = emv.Value,
                        Notes = "EMV"
                    });
                }
            }
        }
        _ = SaveOwnerProfilesAsync();
        // RefreshOwnerOverview touches UI bindings — marshal to the dispatcher (this runs on the webhook
        // async path, off the UI thread). Every other call site already does this (G6b).
        Application.Current?.Dispatcher.BeginInvoke(() =>
            _mainWindowViewModel?.IslandBindings?.RefreshOwnerOverview());
    }

    public async Task<bool> SendWebhookManualAsync(string ownerName)
    {
        if (string.IsNullOrEmpty(ownerName)) return false;
        var profile = GetOwnerProfile(ownerName);
        if (string.IsNullOrWhiteSpace(profile?.WebhookUrl)) return false;

        var message = _mainWindowViewModel?.IslandBindings?.BuildDiscordMessage();
        if (string.IsNullOrEmpty(message)) return false;

        Log.Information("[IslandController] Manual webhook send: owner={Owner}", ownerName);
        return await _webhookService.SendAsync(profile.WebhookUrl, message).ConfigureAwait(false);
    }
}
