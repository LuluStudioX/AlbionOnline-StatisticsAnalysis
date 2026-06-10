using Serilog;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace StatisticsAnalysisTool.Island;

public partial class IslandController
{
    private const string OwnerProfilesFileName = "OwnerProfiles.json";

    // Guards the two session-scoped tracking collections below, which are touched from network-thread
    // push paths; without it concurrent updates can corrupt the dictionary/hashset.
    private readonly object _sessionTrackingLock = new();

    // Tracks which islands have been auto-prefilled today (keyed by owner name).
    // Resets when the UTC date rolls over to prevent double-counting within one cycle.
    private readonly Dictionary<string, (DateTime Date, HashSet<Guid> IslandIds)> _autoPrefillDailyTracker
        = new(StringComparer.OrdinalIgnoreCase);

    // Tracks owners for whom the payment-ready dialog has already been shown this session.
    private readonly HashSet<string> _paymentDialogShownThisSessionForOwners
        = new(StringComparer.OrdinalIgnoreCase);

    private bool TryClaimAutoPrefillSlot(string ownerName, Guid islandId)
    {
        var today = DateTime.UtcNow.Date;
        lock (_sessionTrackingLock)
        {
            if (!_autoPrefillDailyTracker.TryGetValue(ownerName, out var entry) || entry.Date != today)
            {
                entry = (today, new HashSet<Guid>());
                _autoPrefillDailyTracker[ownerName] = entry;
            }
            return entry.IslandIds.Add(islandId);
        }
    }
    private readonly object _ownerProfilesLock = new();
    private Dictionary<string, OwnerProfile> _ownerProfiles = new(StringComparer.OrdinalIgnoreCase);

    public async Task LoadOwnerProfilesAsync()
    {
        var path = AppDataPaths.UserDataFile(OwnerProfilesFileName);
        var loaded = await FileController.LoadAsync<Dictionary<string, OwnerProfile>>(path);
        lock (_ownerProfilesLock)
            _ownerProfiles = loaded ?? new Dictionary<string, OwnerProfile>(StringComparer.OrdinalIgnoreCase);
        MigrateWithdrawalWeekStarts();
        Log.Debug("[IslandController] Loaded {Count} owner profiles.", _ownerProfiles.Count);
    }

    private void MigrateWithdrawalWeekStarts()
    {
        // One-time migration: parse ISO week number from notes (e.g. "Week 13 payment") and
        // set PaidForWeekStart so withdrawals are matched to the correct calendar week.
        var needsSave = false;
        lock (_ownerProfilesLock)
        {
            foreach (var profile in _ownerProfiles.Values)
            {
                foreach (var w in profile.Withdrawals ?? [])
                {
                    if (w.PaidForWeekStart.HasValue) continue;

                    var isoWeek = TryParseWeekNumberFromNotes(w.Notes);
                    if (isoWeek == null) continue;

                    var year = w.Timestamp.Year;
                    var weekStart = IsoWeekToMonday(year, isoWeek.Value);
                    if (weekStart == null) continue;

                    w.PaidForWeekStart = weekStart;
                    needsSave = true;
                }
            }
        }

        if (needsSave)
            _ = SaveOwnerProfilesAsync();
    }

    private static int? TryParseWeekNumberFromNotes(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(notes, @"\bweek\s+(\d{1,2})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        if (int.TryParse(match.Groups[1].Value, out var week) && week >= 1 && week <= 53)
            return week;
        return null;
    }

    private static DateTime? IsoWeekToMonday(int year, int week)
    {
        // ISO 8601: week 1 = week containing first Thursday of year.
        var jan4 = new DateTime(year, 1, 4);
        var dayOfWeek = (int) jan4.DayOfWeek;
        if (dayOfWeek == 0) dayOfWeek = 7; // Sunday = 7
        var week1Monday = jan4.AddDays(1 - dayOfWeek);
        var result = week1Monday.AddDays((week - 1) * 7);
        if (result.Year < year - 1 || result.Year > year + 1) return null;
        return result;
    }

    public async Task SaveOwnerProfilesAsync()
    {
        Dictionary<string, OwnerProfile> snapshot;
        lock (_ownerProfilesLock)
            snapshot = new Dictionary<string, OwnerProfile>(_ownerProfiles, StringComparer.OrdinalIgnoreCase);

        var path = AppDataPaths.UserDataFile(OwnerProfilesFileName);
        DirectoryController.CreateDirectoryWhenNotExists(AppDataPaths.UserDataDirectory);
        await FileController.SaveAsync(snapshot, path);
        Log.Debug("[IslandController] Saved {Count} owner profiles.", snapshot.Count);
    }

    private OwnerProfile GetOrCreateProfile(string ownerName)
    {
        lock (_ownerProfilesLock)
        {
            if (!_ownerProfiles.TryGetValue(ownerName, out var profile))
            {
                profile = new OwnerProfile();
                _ownerProfiles[ownerName] = profile;
            }
            return profile;
        }
    }

    // Non-inserting lookup for read paths. Returns false (and null) when no profile exists yet, so
    // reading a ledger/balance for an unknown owner can never create + persist a phantom profile (G4).
    public bool TryGetProfile(string ownerName, out OwnerProfile profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(ownerName)) return false;
        lock (_ownerProfilesLock)
            return _ownerProfiles.TryGetValue(ownerName, out profile);
    }

    // Read-only profile lookup — never inserts. Use for any pure read; use GetOrCreateOwnerProfile only
    // on an explicit user-initiated write.
    public OwnerProfile GetOwnerProfile(string ownerName)
    {
        TryGetProfile(ownerName, out var profile);
        return profile;
    }

    // Creating lookup for explicit user-initiated writes (setters, payment, cycle records).
    public OwnerProfile GetOrCreateOwnerProfile(string ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName)) return null;
        return GetOrCreateProfile(ownerName);
    }

    public IReadOnlyList<string> GetOwnersMatchingName(string partnerName)
    {
        if (string.IsNullOrWhiteSpace(partnerName)) return [];
        lock (_islandsLock)
            return _islands
                .Where(i => string.Equals(i.Owner, partnerName, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Owner)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    public IReadOnlyList<Island> GetIslandsByOwner(string ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName)) return [];
        lock (_islandsLock)
            return _islands
                .Where(i => string.Equals(i.Owner, ownerName, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    public void RecordPayment(string ownerName, decimal silverAmount, string notes = "", DateTime? paidForWeekStart = null)
    {
        if (string.IsNullOrWhiteSpace(ownerName) || silverAmount <= 0) return;
        var entry = new OwnerWithdrawalEntry
        {
            Timestamp = DateTime.UtcNow,
            Amount = silverAmount,
            Notes = notes ?? string.Empty,
            PaidForWeekStart = paidForWeekStart?.Date
        };
        GetOrCreateProfile(ownerName).Withdrawals.Add(entry);
        Log.Information("[IslandController] Recorded payment: owner={Owner} amount={Amount}", ownerName, silverAmount);
        _ = SaveOwnerProfilesAsync();
    }

    public void AddCycleRecord(string ownerName, OwnerCycleRecord record)
    {
        if (string.IsNullOrEmpty(ownerName)) return;
        lock (_ownerProfilesLock)
            GetOrCreateProfile(ownerName).CycleHistory.Add(record);
        _ = SaveOwnerProfilesAsync();
    }

    public void TryAutoPrefillPayout(Island island)
    {
        var prefs = _mainWindowViewModel?.IslandBindings?.Preferences;
        if (prefs?.AutoPrefillPayouts != true) return;

        var ownerName = island?.Owner;
        if (string.IsNullOrWhiteSpace(ownerName)) return;

        // Per-island dedup: each island counted at most once per UTC day.
        // Resets at midnight so a legitimate second daily cycle (22h period) counts correctly.
        if (!TryClaimAutoPrefillSlot(ownerName, island.Id))
        {
            Log.Debug("[IslandController] AutoPrefill skipped (already counted today): owner={Owner}, island={Island}",
                ownerName, island.Name);
            return;
        }

        var profile = GetOrCreateProfile(ownerName);
        var pay = island.ManagementPayOverride ?? profile.DefaultPayPerIsland;

        var today = DateTime.UtcNow.Date;

        lock (_ownerProfilesLock)
        {
            if (prefs.AggregateAutoRecordsDaily)
            {
                // Aggregate into ANY today's Islands record (manual or auto-prefilled).
                var existing = profile.CycleHistory
                    .Where(c => c.Date == today && c.RecordType == CycleRecordType.Islands)
                    .OrderByDescending(c => c.Date)
                    .FirstOrDefault();

                if (existing != null)
                {
                    existing.IslandCount++;
                    existing.EarnedAmount += pay;

                    // Append auto-prefill marker to notes if not already present.
                    if (!existing.Notes.Contains(AutoPrefillNotesMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        existing.Notes = string.IsNullOrWhiteSpace(existing.Notes)
                            ? AutoPrefillNotesMarker
                            : $"{existing.Notes}; {AutoPrefillNotesMarker}";
                    }

                    Log.Information("[IslandController] AutoPrefill aggregated into existing record: owner={Owner}, island={Island}, count={Count}, earned={Earned}",
                        ownerName, island.Name, existing.IslandCount, existing.EarnedAmount);
                }
                else
                {
                    profile.CycleHistory.Add(new OwnerCycleRecord
                    {
                        Date = today,
                        RecordType = CycleRecordType.Islands,
                        IslandCount = 1,
                        EarnedAmount = pay,
                        Notes = AutoPrefillNotesMarker
                    });
                    Log.Information("[IslandController] AutoPrefill created record: owner={Owner}, island={Island}, earned={Earned}",
                        ownerName, island.Name, pay);
                }
            }
            else
            {
                profile.CycleHistory.Add(new OwnerCycleRecord
                {
                    Date = today,
                    RecordType = CycleRecordType.Islands,
                    IslandCount = 1,
                    EarnedAmount = pay,
                    Notes = AutoPrefillNotesMarker
                });
                Log.Information("[IslandController] AutoPrefill created record: owner={Owner}, island={Island}, earned={Earned}",
                    ownerName, island.Name, pay);
            }
        }

        _ = SaveOwnerProfilesAsync();

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _mainWindowViewModel?.IslandBindings?.RefreshOwnerOverview();
        });

        TryShowPaymentReadyDialog(ownerName);
    }

    private void TryShowPaymentReadyDialog(string ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName)) return;
        lock (_sessionTrackingLock)
            if (!_paymentDialogShownThisSessionForOwners.Add(ownerName)) return;

        var totalIslands = GetIslandsByOwner(ownerName).Count();
        if (totalIslands == 0) return;

        lock (_sessionTrackingLock)
        {
            if (!_autoPrefillDailyTracker.TryGetValue(ownerName, out var entry)
                || entry.Date != DateTime.UtcNow.Date
                || entry.IslandIds.Count < totalIslands)
            {
                _paymentDialogShownThisSessionForOwners.Remove(ownerName);
                return;
            }
        }

        Log.Information("[IslandController] All {Count} islands done for owner {Owner} — opening payment dialog", totalIslands, ownerName);

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var profile = GetOwnerProfile(ownerName);
            if (string.IsNullOrWhiteSpace(profile?.WebhookUrl)) return;

            var dialog = new Views.WebhookConfirmDialog(ownerName)
            {
                Owner = Application.Current.MainWindow,
                Topmost = true
            };
            dialog.Submitted += async (notes, emv) =>
            {
                if (!string.IsNullOrWhiteSpace(notes) || emv.HasValue)
                {
                    lock (_ownerProfilesLock)
                    {
                        var today = DateTime.Today;
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
                            profile.CycleHistory?.Add(new OwnerCycleRecord
                            {
                                Date = today,
                                RecordType = CycleRecordType.Other,
                                EarnedAmount = emv.Value,
                                Notes = "EMV"
                            });
                        }
                    }
                    _ = SaveOwnerProfilesAsync();
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                        _mainWindowViewModel?.IslandBindings?.RefreshOwnerOverview());
                }

                var message = _mainWindowViewModel?.IslandBindings?.BuildDiscordMessage(ownerName);
                if (!string.IsNullOrEmpty(message))
                    await DiscordWebhookService.SendAsync(profile.WebhookUrl, message).ConfigureAwait(false);
            };
            dialog.Show();
        });
    }

    private const string AutoPrefillNotesMarker = "Auto-prefilled";

    public void UpdateCycleRecord(string ownerName, OwnerCycleRecord updated)
    {
        if (string.IsNullOrEmpty(ownerName)) return;
        lock (_ownerProfilesLock)
        {
            var profile = GetOrCreateProfile(ownerName);
            var existing = profile.CycleHistory?.FirstOrDefault(c => c.Id == updated.Id);
            if (existing == null) return;
            existing.Date = updated.Date;
            existing.IslandCount = updated.IslandCount;
            existing.EarnedAmount = updated.EarnedAmount;
            existing.Notes = updated.Notes;
        }
        _ = SaveOwnerProfilesAsync();
    }

    public void UpdateWithdrawalEntry(string ownerName, OwnerWithdrawalEntry updated)
    {
        if (string.IsNullOrEmpty(ownerName)) return;
        lock (_ownerProfilesLock)
        {
            var profile = GetOrCreateProfile(ownerName);
            var existing = profile.Withdrawals?.FirstOrDefault(w => w.Id == updated.Id);
            if (existing == null) return;
            existing.Timestamp = updated.Timestamp;
            existing.Amount = updated.Amount;
            existing.Notes = updated.Notes;
        }
        _ = SaveOwnerProfilesAsync();
    }
}
