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

    // The island laborer cycle is 22h. Auto-prefill dedup uses this as the minimum gap between two
    // counts of the same island, so a legitimate second cycle that lands on the SAME calendar day
    // (e.g. collect 01:00 -> ready 23:00) still counts, while an accidental double-trigger is blocked.
    private static readonly TimeSpan AutoPrefillCycleGap = TimeSpan.FromHours(22);

    // Tracks the last instant each island was auto-prefilled (owner name -> island id -> UTC time).
    // Time-based, not calendar-based: a calendar-day key cannot represent two cycles within one day.
    private readonly Dictionary<string, Dictionary<Guid, DateTime>> _autoPrefillDailyTracker
        = new(StringComparer.OrdinalIgnoreCase);

    // Tracks owners for whom the payment-ready dialog has already been shown this session.
    private readonly HashSet<string> _paymentDialogShownThisSessionForOwners
        = new(StringComparer.OrdinalIgnoreCase);

    private bool TryClaimAutoPrefillSlot(string ownerName, Guid islandId)
    {
        // UTC instant for the gap math: point-in-time, timezone-agnostic. (Day bucketing of records
        // and counters lives in IslandTime.Today; this dedup gate only enforces the 22h cycle spacing.)
        var now = DateTime.UtcNow;
        lock (_sessionTrackingLock)
        {
            if (!_autoPrefillDailyTracker.TryGetValue(ownerName, out var islands))
            {
                islands = new Dictionary<Guid, DateTime>();
                _autoPrefillDailyTracker[ownerName] = islands;
            }

            if (islands.TryGetValue(islandId, out var last) && now - last < AutoPrefillCycleGap)
                return false;

            islands[islandId] = now;
            return true;
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

        // Per-island dedup: an island re-counts only after the full 22h cycle gap (see TryClaimAutoPrefillSlot),
        // so a legitimate second daily cycle still counts while an accidental double-trigger does not.
        if (!TryClaimAutoPrefillSlot(ownerName, island.Id))
        {
            Log.Debug("[IslandController] AutoPrefill skipped (already counted today): owner={Owner}, island={Island}",
                ownerName, island.Name);
            return;
        }

        var profile = GetOrCreateProfile(ownerName);
        var pay = island.ManagementPayOverride ?? profile.DefaultPayPerIsland;

        // Stamp on the Albion (UTC) accounting day so the record and the "Done today" counter agree.
        var today = IslandTime.Today;

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
        var prefs = _mainWindowViewModel?.IslandBindings?.Preferences;
        if (prefs?.AutoPrefillPayouts != true) return;
        lock (_sessionTrackingLock)
            if (!_paymentDialogShownThisSessionForOwners.Add(ownerName)) return;

        var totalIslands = GetIslandsByOwner(ownerName).Count();
        if (totalIslands == 0) return;

        lock (_sessionTrackingLock)
        {
            var cutoff = DateTime.UtcNow - AutoPrefillCycleGap;
            var cycledCount = _autoPrefillDailyTracker.TryGetValue(ownerName, out var islands)
                ? islands.Values.Count(t => t >= cutoff)
                : 0;
            if (cycledCount < totalIslands)
            {
                _paymentDialogShownThisSessionForOwners.Remove(ownerName);
                return;
            }
        }

        // Payout is recorded on an island's FIRST laborer, so the gate above can pass while THIS island
        // still has laborers home. Wait until all are back out (none tracked => nothing to wait for).
        var currentSnapshots = _snapshots.Values.ToList();
        if (currentSnapshots.Count > 0 && !currentSnapshots.All(s => s.IsOnJob))
        {
            lock (_sessionTrackingLock)
                _paymentDialogShownThisSessionForOwners.Remove(ownerName);
            return;
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
                    ApplyDailyNotesAndEmvToTodayRecord(profile, notes, emv);
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

    // EMV goes into Notes, not EarnedAmount - that field holds the fixed silver payment, not loot value.
    // The bare auto-prefill marker is a placeholder, replaced by the addition; real notes are appended to.
    private void ApplyDailyNotesAndEmvToTodayRecord(OwnerProfile profile, string notes, decimal? emv)
    {
        if (profile == null) return;

        var additions = new List<string>();
        if (!string.IsNullOrWhiteSpace(notes)) additions.Add(notes.Trim());
        if (emv.HasValue) additions.Add($"{emv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} EMV");
        if (additions.Count == 0) return;

        var addition = string.Join("; ", additions);

        lock (_ownerProfilesLock)
        {
            var today = IslandTime.Today;
            var record = profile.CycleHistory?
                .FirstOrDefault(c => c.Date.Date == today && c.RecordType == CycleRecordType.Islands);
            if (record == null) return;

            record.Notes = string.IsNullOrWhiteSpace(record.Notes)
                           || string.Equals(record.Notes.Trim(), AutoPrefillNotesMarker, StringComparison.OrdinalIgnoreCase)
                ? addition
                : $"{record.Notes}; {addition}";
        }
    }

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
