using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Common.UserSettings;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network.Manager;
using StatisticsAnalysisTool.ViewModels;
using System.Windows;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;

namespace StatisticsAnalysisTool.Models.BindingModel;

public partial class IslandBindings
{
    // ── Owner Overview ────────────────────────────────────────────────────────

    private string _selectedOverviewOwner = string.Empty;
    private DateTime _lastOwnerRefreshUtc = DateTime.MinValue;
    private double _selectedOwnerGraphHeight = 180;
    private int? _chartWindowDays = null;
    private string _newWithdrawalAmount = string.Empty;
    private string _newWithdrawalNote = string.Empty;
    private DateTime _newWithdrawalDate = DateTime.Today;
    private DateTime? _newWithdrawalPaidForWeekStart = null;
    private string _newCycleIslandCount = string.Empty;
    private string _newCycleAmount = string.Empty;
    private string _newCycleNote = string.Empty;
    private DateTime _newCycleDate = DateTime.Today;
    private CycleRecordType _newCycleType = CycleRecordType.Islands;

    public IEnumerable<string> OwnerOptions => Islands
        .Where(i => !string.IsNullOrWhiteSpace(i.OwnerName))
        .Select(i => i.OwnerName.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(o => o, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string SelectedOverviewOwner
    {
        get => _selectedOverviewOwner;
        set
        {
            var incoming = value?.Trim() ?? string.Empty;
            var normalized = string.Join("|",
                incoming.Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.Equals(_selectedOverviewOwner, normalized, StringComparison.Ordinal)) return;
            _selectedOverviewOwner = normalized;
            OnPropertyChanged();
            RefreshOwnerOverview();
        }
    }

    private IEnumerable<string> GetEffectiveOwnerNames()
    {
        if (Preferences?.AllowMultiOwnerSelection == true && !string.IsNullOrWhiteSpace(_selectedOverviewOwner))
        {
            return _selectedOverviewOwner
                .Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }
        var single = !string.IsNullOrWhiteSpace(_selectedOverviewOwner)
            ? _selectedOverviewOwner
            : SelectedIsland?.OwnerName;
        return string.IsNullOrWhiteSpace(single)
            ? Enumerable.Empty<string>()
            : new[] { single.Trim() };
    }

    public string EffectiveOwnerName
    {
        get
        {
            var names = GetEffectiveOwnerNames().ToList();
            return names.Count == 1 ? names[0] : string.Empty;
        }
    }

    private bool IsMultiOwnerSelection => GetEffectiveOwnerNames().Skip(1).Any();
    public bool HasSelectedOwner => GetEffectiveOwnerNames().Any();

    private IslandController GetController() =>
        ServiceLocator.Resolve<TrackingController>()?.IslandController;

    private OwnerProfile GetOrCreateProfile(string ownerName) =>
        GetController()?.GetOwnerProfile(ownerName) ?? new OwnerProfile();

    private IEnumerable<Island.Island> GetSelectedOwnerDomainIslands()
    {
        var controller = GetController();
        if (controller == null) return Enumerable.Empty<Island.Island>();
        var owners = GetEffectiveOwnerNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (owners.Count == 0) return Enumerable.Empty<Island.Island>();
        return owners.SelectMany(o => controller.GetIslandsByOwner(o));
    }

    private static decimal GetEffectiveIslandPay(Island.Island island, OwnerProfile profile)
    {
        if (island == null) return 0;
        if (island.ManagementPayOverride.HasValue) return island.ManagementPayOverride.Value;
        return profile?.DefaultPayPerIsland ?? 0;
    }

    // --- Computed owner stats ---

    public int SelectedOwnerIslandCount => GetSelectedOwnerDomainIslands().Count();

    public int SelectedOwnerDoneTodayCount => GetSelectedOwnerDomainIslands()
        .Count(i => i.LastPlantedAt.HasValue &&
                    i.LastPlantedAt.Value.ToUniversalTime() >= DateTime.UtcNow.AddHours(-22));

    public int SelectedOwnerLeftTodayCount => Math.Max(0, SelectedOwnerIslandCount - SelectedOwnerDoneTodayCount);

    // --- Global handling-time stats (used in main status bar, across all islands) ---

    private static bool IsDoneThisCycle(Island.Island isl)
        => isl.LastPlantedAt.HasValue &&
           isl.LastPlantedAt.Value.ToUniversalTime() >= DateTime.UtcNow.AddHours(-22);

    private int GetEffectiveVisitMinutes(Island.Island isl)
        => isl.VisitDurationMinutes ?? Preferences.DefaultVisitDurationMinutes;

    public int GlobalIslandsDoneTodayCount
    {
        get
        {
            var controller = GetController();
            if (controller == null) return 0;
            return controller.Islands.Count(IsDoneThisCycle);
        }
    }

    public int GlobalIslandsLeftTodayCount
    {
        get
        {
            var controller = GetController();
            if (controller == null) return 0;
            return Math.Max(0, controller.Islands.Count - GlobalIslandsDoneTodayCount);
        }
    }

    public string GlobalHandlingTimeStatusText
    {
        get
        {
            var controller = GetController();
            if (controller == null) return string.Empty;
            var all = controller.Islands;
            var total = all.Count;
            if (total == 0) return string.Empty;

            var done = all.Count(IsDoneThisCycle);
            // Only count islands that need a visit now (ready or never planted) — not ones still growing
            var needsVisitNow = all.Where(i => !IsDoneThisCycle(i) && i.NeedsVisit).ToList();

            var leftMinutes = needsVisitNow.Sum(GetEffectiveVisitMinutes);
            var ts = TimeSpan.FromMinutes(leftMinutes);
            var timeText = ts.TotalMinutes == 0
                ? "0m"
                : ts.TotalHours >= 1
                    ? $"{(int) ts.TotalHours}h {ts.Minutes}m"
                    : $"{ts.Minutes}m";

            return $"{done}/{total} done · ~{timeText} left";
        }
    }

    public decimal SelectedOwnerDailyPotentialPay
    {
        get
        {
            var names = GetEffectiveOwnerNames().ToList();
            var controller = GetController();
            if (controller == null || names.Count == 0) return 0;
            return GetSelectedOwnerDomainIslands()
                .Sum(i => GetEffectiveIslandPay(i,
                    controller.GetOwnerProfile(i.Owner?.Trim() ?? string.Empty)));
        }
    }

    public decimal SelectedOwnerTotalCycleEarned
    {
        get
        {
            return GetEffectiveOwnerNames()
                .Sum(n => GetOrCreateProfile(n).CycleHistory?.Sum(c => c.EarnedAmount) ?? 0);
        }
    }

    public decimal SelectedOwnerTotalWithdrawn
    {
        get
        {
            return GetEffectiveOwnerNames()
                .Sum(n => GetOrCreateProfile(n).Withdrawals?.Sum(w => w.Amount) ?? 0);
        }
    }

    public decimal SelectedOwnerOpeningBalance
    {
        get => GetEffectiveOwnerNames().Sum(n => GetOrCreateProfile(n).OpeningBalance);
        set
        {
            var name = EffectiveOwnerName;
            if (string.IsNullOrWhiteSpace(name)) return;
            GetOrCreateProfile(name).OpeningBalance = value;
            OnPropertyChanged();
            RefreshOwnerOverview();
        }
    }

    public decimal SelectedOwnerBalance => SelectedOwnerOpeningBalance + SelectedOwnerTotalCycleEarned - SelectedOwnerTotalWithdrawn;

    public decimal SelectedOwnerTodayLiveEstimate
    {
        get
        {
            var controller = GetController();
            if (controller == null) return 0;
            return GetSelectedOwnerDomainIslands()
                .Where(i => i.LastPlantedAt.HasValue &&
                            i.LastPlantedAt.Value.ToUniversalTime() >= DateTime.UtcNow.AddHours(-22))
                .Sum(i => GetEffectiveIslandPay(i, controller.GetOwnerProfile(i.Owner?.Trim() ?? string.Empty)));
        }
    }

    public int SelectedOwnerTodayLiveCycledCount => SelectedOwnerDoneTodayCount;
    public bool SelectedOwnerCanQuickFill => SelectedOwnerTodayLiveCycledCount > 0;

    public decimal SelectedOwnerDefaultPayPerIsland
    {
        get
        {
            var name = EffectiveOwnerName;
            return string.IsNullOrWhiteSpace(name) ? 0 : GetOrCreateProfile(name).DefaultPayPerIsland;
        }
        set
        {
            var name = EffectiveOwnerName;
            if (string.IsNullOrWhiteSpace(name)) return;
            GetOrCreateProfile(name).DefaultPayPerIsland = value < 0 ? 0 : value;
            OnPropertyChanged();
            RefreshOwnerOverview();
        }
    }

    public DayOfWeek SelectedOwnerPayoutDay
    {
        get
        {
            var name = EffectiveOwnerName;
            return string.IsNullOrWhiteSpace(name) ? DayOfWeek.Sunday : GetOrCreateProfile(name).PayoutDayOfWeek;
        }
        set
        {
            var name = EffectiveOwnerName;
            if (string.IsNullOrWhiteSpace(name)) return;
            GetOrCreateProfile(name).PayoutDayOfWeek = value;
            OnPropertyChanged();
            RefreshOwnerOverview();
        }
    }

    public IEnumerable<DayOfWeek> PayoutDayOptions =>
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static IEnumerable<DayOfWeek> WeekStartOptions =>
    [
        DayOfWeek.Monday, DayOfWeek.Sunday
    ];

    public static IEnumerable<OwnerEngagementType> EngagementTypeOptions =>
    [
        OwnerEngagementType.Unpaid, OwnerEngagementType.PaidManager, OwnerEngagementType.Rented
    ];

    public OwnerEngagementType SelectedOwnerEngagementType
    {
        get
        {
            var name = EffectiveOwnerName;
            return string.IsNullOrWhiteSpace(name) ? OwnerEngagementType.Unpaid : GetOrCreateProfile(name).EngagementType;
        }
        set
        {
            var name = EffectiveOwnerName;
            if (string.IsNullOrWhiteSpace(name)) return;
            GetOrCreateProfile(name).EngagementType = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPaidManagerEngagement));
            RefreshManagerResponsibilityItems();
            SaveOwnerProfile();
        }
    }

    public bool IsPaidManagerEngagement => SelectedOwnerEngagementType == OwnerEngagementType.PaidManager;

    private ObservableCollection<ManagerResponsibilityItem> _managerResponsibilityItems = [];
    public ObservableCollection<ManagerResponsibilityItem> ManagerResponsibilityItems => _managerResponsibilityItems;

    private void RefreshManagerResponsibilityItems()
    {
        var name = EffectiveOwnerName;
        var current = string.IsNullOrWhiteSpace(name) ? ManagerResponsibility.None : GetOrCreateProfile(name).ManagerResponsibilities;
        _managerResponsibilityItems.Clear();
        foreach (var flag in new[] { ManagerResponsibility.HandlesRefills, ManagerResponsibility.NotifyLowResources, ManagerResponsibility.RequestsMaterials })
        {
            var item = new ManagerResponsibilityItem(flag, current.HasFlag(flag));
            item.PropertyChanged += OnManagerResponsibilityItemChanged;
            _managerResponsibilityItems.Add(item);
        }
        OnPropertyChanged(nameof(ManagerResponsibilityItems));
    }

    private void OnManagerResponsibilityItemChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ManagerResponsibilityItem.IsSelected)) return;
        var name = EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(name)) return;
        var combined = ManagerResponsibility.None;
        foreach (var item in _managerResponsibilityItems)
            if (item.IsSelected) combined |= item.Flag;
        GetOrCreateProfile(name).ManagerResponsibilities = combined;
        SaveOwnerProfile();
    }

    public string SelectedOwnerNotes
    {
        get
        {
            var name = EffectiveOwnerName;
            return string.IsNullOrWhiteSpace(name) ? string.Empty : GetOrCreateProfile(name).Notes;
        }
        set
        {
            var name = EffectiveOwnerName;
            if (string.IsNullOrWhiteSpace(name)) return;
            GetOrCreateProfile(name).Notes = value ?? string.Empty;
            OnPropertyChanged();
            SaveOwnerProfile();
        }
    }

    public string SelectedOwnerWebhookUrl
    {
        get
        {
            var name = EffectiveOwnerName;
            return string.IsNullOrWhiteSpace(name) ? string.Empty : GetOrCreateProfile(name).WebhookUrl;
        }
        set
        {
            var name = EffectiveOwnerName;
            if (string.IsNullOrWhiteSpace(name)) return;
            GetOrCreateProfile(name).WebhookUrl = value ?? string.Empty;
            OnPropertyChanged();
            SaveOwnerProfile();
        }
    }

    public decimal SelectedOwnerTodayRecordedEarned
    {
        get
        {
            var today = DateTime.Today;
            return GetEffectiveOwnerNames()
                .Sum(n => GetOrCreateProfile(n).CycleHistory?
                    .Where(c => c.Date.Date == today && c.RecordType == CycleRecordType.Islands)
                    .Sum(c => c.EarnedAmount) ?? 0);
        }
    }

    public int SelectedOwnerTodayRecordedIslandCount
    {
        get
        {
            var today = DateTime.Today;
            return GetEffectiveOwnerNames()
                .Sum(n => GetOrCreateProfile(n).CycleHistory?
                    .Where(c => c.Date.Date == today)
                    .Sum(c => c.IslandCount) ?? 0);
        }
    }

    public bool AllIslandsDoneToday =>
        SelectedOwnerIslandCount > 0 && SelectedOwnerTodayRecordedIslandCount >= SelectedOwnerIslandCount;

    public decimal SelectedOwnerTodayExtraEarned
    {
        get
        {
            var today = DateTime.Today;
            return GetEffectiveOwnerNames()
                .Sum(n => GetOrCreateProfile(n).CycleHistory?
                    .Where(c => c.Date.Date == today && c.RecordType != CycleRecordType.Islands)
                    .Sum(c => c.EarnedAmount) ?? 0);
        }
    }

    public IReadOnlyList<string> SelectedOwnerTodayExtraNotes
    {
        get
        {
            var today = DateTime.Today;
            return GetEffectiveOwnerNames()
                .SelectMany(n => GetOrCreateProfile(n).CycleHistory?
                    .Where(c => c.Date.Date == today && c.RecordType != CycleRecordType.Islands)
                    .Select(c => string.IsNullOrWhiteSpace(c.Notes)
                        ? c.RecordType.ToString()
                        : $"{c.RecordType}: {c.Notes}")
                    ?? [])
                .ToList()
                .AsReadOnly();
        }
    }

    public string BuildDiscordMessage(string overrideOwner = null)
    {
        var prevOwner = _selectedOverviewOwner;
        if (!string.IsNullOrWhiteSpace(overrideOwner))
            _selectedOverviewOwner = overrideOwner.Trim();

        try
        {
            return BuildDiscordMessageCore();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(overrideOwner))
                _selectedOverviewOwner = prevOwner;
        }
    }

    private string BuildDiscordMessageCore()
    {
        var name = EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var done = SelectedOwnerTodayRecordedIslandCount;
        var total = SelectedOwnerIslandCount;
        var left = Math.Max(0, total - done);
        var todayEarned = SelectedOwnerTodayRecordedEarned;
        var periodEarned = SelectedOwnerCurrentPeriodRecordedEarned;

        var title = left == 0
            ? $"✅ {name} — all islands are ready"
            : $"⏳ {name} — {left} island(s) remaining";

        var today = DateTime.Today;
        var islandNotes = GetEffectiveOwnerNames()
            .SelectMany(n => GetOrCreateProfile(n).CycleHistory?
                .Where(c => c.Date.Date == today
                    && c.RecordType == CycleRecordType.Islands
                    && !string.IsNullOrWhiteSpace(c.Notes))
                .Select(c => c.Notes) ?? [])
            .Distinct()
            .ToList();

        var extraEarned = SelectedOwnerTodayExtraEarned;
        var extraNotes = SelectedOwnerTodayExtraNotes;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(title);
        var importCode = BuildImportCode();
        var displayCode = importCode.StartsWith("SAT:", StringComparison.Ordinal)
            ? importCode["SAT:".Length..]
            : importCode;
        sb.AppendLine($"`{displayCode}`");
        sb.AppendLine();
        sb.AppendLine($"**Done today** {done}/{total}");
        sb.AppendLine($"**Today's payment** {todayEarned:N0}");
        if (extraEarned > 0)
            sb.AppendLine($"**Today's Extra $$** {extraEarned:N0}");
        sb.AppendLine($"**Accrued this period** {periodEarned:N0}");

        var unpaidWeeks = GetUnpaidCompletedWeeks();
        var netPayable = unpaidWeeks.Sum(w => w.Unpaid);

        if (netPayable > 0 && unpaidWeeks.Count > 1)
            sb.AppendLine($"**Net payable** `{netPayable:N0}`");

        if (extraNotes.Count > 0)
            sb.AppendLine($"**Extra notes** {string.Join("; ", extraNotes)}");
        if (islandNotes.Count > 0)
            sb.AppendLine($"**Notes** {string.Join("; ", islandNotes)}");

        if (unpaidWeeks.Count > 0)
        {
            sb.AppendLine();
            foreach (var week in unpaidWeeks)
                sb.AppendLine($"⚠ **Unpaid week {week.WeekLabel}👇** ```{week.Unpaid:N0}```");
        }

        return sb.ToString().TrimEnd();
    }

    public string DiscordEmbedPreview => BuildDiscordMessage();

    public string BuildImportCode()
    {
        var done = SelectedOwnerTodayRecordedIslandCount;
        var earned = SelectedOwnerTodayRecordedEarned;
        var period = SelectedOwnerCurrentPeriodRecordedEarned;
        var date = DateTime.Today.ToString("yyyy-MM-dd");
        var raw = $"I{done}/E{(long) earned}/P{(long) period}/D{date}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        return $"SAT:{encoded}";
    }

    public bool TryImportCode(string code, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            error = "Code is empty.";
            return false;
        }

        code = code.Trim();
        if (!code.StartsWith("SAT:", StringComparison.OrdinalIgnoreCase))
        {
            error = "Not a valid SAT import code (must start with SAT:).";
            return false;
        }

        string payload;
        try
        {
            var b64 = code["SAT:".Length..];
            payload = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch
        {
            error = "Code is malformed (invalid encoding).";
            return false;
        }

        var parts = payload.Split('/');
        int? islandCount = null;
        decimal? earned = null;
        DateTime? date = null;

        foreach (var part in parts)
        {
            if (part.StartsWith("I", StringComparison.OrdinalIgnoreCase) && int.TryParse(part[1..], out var i))
                islandCount = i;
            else if (part.StartsWith("E", StringComparison.OrdinalIgnoreCase) && decimal.TryParse(part[1..], out var e))
                earned = e;
            else if (part.StartsWith("D", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(part[1..], out var d))
                date = d;
        }

        if (!islandCount.HasValue || !earned.HasValue || !date.HasValue)
        {
            error = "Code is incomplete or malformed.";
            return false;
        }

        var ownerName = EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(ownerName))
        {
            error = "No owner selected.";
            return false;
        }

        var controller = GetController();
        if (controller == null)
        {
            error = "Island controller unavailable.";
            return false;
        }

        controller.AddCycleRecord(ownerName, new OwnerCycleRecord
        {
            Date = date.Value.Date,
            IslandCount = islandCount.Value,
            EarnedAmount = earned.Value,
            Notes = "Imported via SAT code"
        });

        RefreshOwnerOverview();
        return true;
    }

    public DateTime SelectedOwnerNextPayoutDate
    {
        get
        {
            if (Preferences?.UseDailyPayoutMode == true) return DateTime.Today;
            var today = DateTime.Today;
            var dayDelta = ((int) SelectedOwnerPayoutDay - (int) today.DayOfWeek + 7) % 7;
            return today.AddDays(dayDelta);
        }
    }

    public string SelectedOwnerPayoutScheduleText
    {
        get
        {
            if (Preferences?.UseDailyPayoutMode == true) return "Daily";
            var weekStart = Preferences?.WeekStartDay ?? DayOfWeek.Monday;
            var today = DateTime.Today;
            var dayDelta = ((int) today.DayOfWeek - (int) weekStart + 7) % 7;
            var periodStart = today.AddDays(-dayDelta);
            var periodEnd = periodStart.AddDays(6);
            return $"Weekly: {periodStart:ddd dd MMM} - {periodEnd:ddd dd MMM}";
        }
    }

    public DateTime SelectedOwnerCurrentPeriodStartDate
    {
        get
        {
            if (Preferences?.UseDailyPayoutMode == true) return DateTime.Today;
            var name = EffectiveOwnerName;
            if (string.IsNullOrWhiteSpace(name)) return DateTime.Today;
            var profile = GetOrCreateProfile(name);
            var lastPayout = profile.Withdrawals?
                .Select(w => w.Timestamp.ToLocalTime().Date)
                .OrderByDescending(d => d).FirstOrDefault();
            return lastPayout.HasValue && lastPayout.Value != default ? lastPayout.Value.AddDays(1) : SelectedOwnerNextPayoutDate.AddDays(-6);
        }
    }

    public decimal SelectedOwnerCurrentPeriodRecordedEarned
    {
        get
        {
            var name = EffectiveOwnerName;
            if (string.IsNullOrWhiteSpace(name)) return 0;
            var profile = GetOrCreateProfile(name);
            if (Preferences?.UseDailyPayoutMode == true)
                return profile.CycleHistory?.Where(c => c.Date.Date == DateTime.Today).Sum(c => c.EarnedAmount) ?? 0;
            var weekStart = GetCurrentCalendarWeekStart();
            return profile.CycleHistory?
                .Where(c => c.Date.Date >= weekStart && c.Date.Date <= DateTime.Today)
                .Sum(c => c.EarnedAmount) ?? 0;
        }
    }

    private DateTime GetCurrentCalendarWeekStart()
    {
        var weekStartDay = Preferences?.WeekStartDay ?? DayOfWeek.Monday;
        var today = DateTime.Today;
        var delta = ((int) today.DayOfWeek - (int) weekStartDay + 7) % 7;
        return today.AddDays(-delta);
    }

    private (DateTime WeekStart, DateTime WeekEnd) GetCalendarWeekBounds(DateTime date)
    {
        var weekStartDay = Preferences?.WeekStartDay ?? DayOfWeek.Monday;
        var delta = ((int) date.DayOfWeek - (int) weekStartDay + 7) % 7;
        var start = date.Date.AddDays(-delta);
        return (start, start.AddDays(6));
    }

    private List<(string WeekLabel, decimal Unpaid)> GetUnpaidCompletedWeeks()
    {
        var result = new List<(string WeekLabel, decimal Unpaid)>();
        var names = GetEffectiveOwnerNames().ToList();
        if (names.Count == 0) return result;

        var currentWeekStart = GetCurrentCalendarWeekStart();

        var allHistory = names
            .SelectMany(n => GetOrCreateProfile(n).CycleHistory ?? Enumerable.Empty<OwnerCycleRecord>())
            .Where(c => c.Date.Date < currentWeekStart)
            .ToList();

        var allWithdrawals = names
            .SelectMany(n => GetOrCreateProfile(n).Withdrawals ?? Enumerable.Empty<OwnerWithdrawalEntry>())
            .Where(w => w.Timestamp.ToLocalTime().Date < currentWeekStart)
            .ToList();

        var weeklyEarned = allHistory
            .GroupBy(c => GetCalendarWeekBounds(c.Date.Date).WeekStart)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.EarnedAmount));

        var weeklyWithdrawn = allWithdrawals
            .GroupBy(w => w.PaidForWeekStart?.Date ?? GetCalendarWeekBounds(w.Timestamp.ToLocalTime().Date).WeekStart)
            .ToDictionary(g => g.Key, g => g.Sum(w => w.Amount));

        // Process weeks chronologically. Overpay in week N can only offset underpays in weeks <= N.
        var allWeeks = weeklyEarned.Keys
            .Union(weeklyWithdrawn.Keys)
            .OrderBy(w => w)
            .ToList();

        decimal runningPool = 0;
        var pendingUnpaid = new List<(DateTime WeekStart, decimal Unpaid)>();

        foreach (var ws in allWeeks)
        {
            weeklyEarned.TryGetValue(ws, out var earned);
            weeklyWithdrawn.TryGetValue(ws, out var withdrawn);
            var delta = earned - withdrawn;

            if (delta < 0)
            {
                // Overpay: absorb into pool, apply to oldest pending underpays first.
                runningPool += -delta;
                var remaining = new List<(DateTime WeekStart, decimal Unpaid)>();
                foreach (var (pendingWs, pendingUnpaidAmt) in pendingUnpaid)
                {
                    var net = pendingUnpaidAmt - runningPool;
                    runningPool = Math.Max(0, runningPool - pendingUnpaidAmt);
                    if (net > 0) remaining.Add((pendingWs, net));
                }
                pendingUnpaid = remaining;
            }
            else if (delta > 0)
            {
                pendingUnpaid.Add((ws, delta));
            }
        }

        // Apply any remaining pool to future underpays (overpay landed before the underpay week).
        foreach (var (weekStart, unpaid) in pendingUnpaid)
        {
            var net = unpaid - runningPool;
            runningPool = Math.Max(0, runningPool - unpaid);
            if (net <= 0) continue;
            var (weekStartBound, weekEndBound) = GetCalendarWeekBounds(weekStart);
            result.Add(($"{weekStartBound:dd MMM} – {weekEndBound:dd MMM}", net));
        }

        return result;
    }

    public int SelectedOwnerDaysUntilPayout =>
        Math.Max(0, (SelectedOwnerNextPayoutDate - DateTime.Today).Days);

    public decimal SelectedOwnerProjectedAgreementPayout =>
        Preferences?.UseDailyPayoutMode == true
            ? SelectedOwnerCurrentPeriodRecordedEarned
            : SelectedOwnerCurrentPeriodRecordedEarned + (SelectedOwnerDaysUntilPayout * SelectedOwnerDailyPotentialPay);

    public decimal SelectedOwnerProjectedWeeklyIncome => SelectedOwnerDailyPotentialPay * 7;

    // Earnings recorded before the current period that haven't been withdrawn yet.
    // Non-zero when a payout period rolls over without a recorded withdrawal.
    public decimal SelectedOwnerUnpaidPreviousEarned =>
        Math.Max(0, SelectedOwnerBalance - SelectedOwnerCurrentPeriodRecordedEarned);

    public bool HasUnpaidPreviousEarned => SelectedOwnerUnpaidPreviousEarned > 0;

    public OwnerProfile SelectedOwnerProfile => string.IsNullOrWhiteSpace(EffectiveOwnerName)
        ? null
        : GetOrCreateProfile(EffectiveOwnerName);

    public IReadOnlyList<OwnerLedgerEntry> SelectedOwnerLedger
    {
        get
        {
            var name = EffectiveOwnerName;
            if (string.IsNullOrWhiteSpace(name)) return Array.Empty<OwnerLedgerEntry>();
            var profile = GetOrCreateProfile(name);
            var entries = new List<OwnerLedgerEntry>();
            foreach (var c in profile.CycleHistory ?? Enumerable.Empty<OwnerCycleRecord>())
            {
                entries.Add(new OwnerLedgerEntry
                {
                    Id = c.Id, Date = c.Date,
                    Type = c.RecordType == CycleRecordType.Islands ? "Earned" : c.RecordType.ToString(),
                    IslandCount = c.RecordType == CycleRecordType.Islands && c.IslandCount > 0 ? c.IslandCount : null,
                    Amount = c.EarnedAmount, Notes = c.Notes
                });
            }
            foreach (var w in profile.Withdrawals ?? Enumerable.Empty<OwnerWithdrawalEntry>())
            {
                entries.Add(new OwnerLedgerEntry
                {
                    Id = w.Id, Date = w.Timestamp.ToLocalTime(), Type = "Traded",
                    Amount = -w.Amount, Notes = w.Notes
                });
            }
            return entries.OrderByDescending(e => e.Date).ToList();
        }
    }

    public bool SelectedOwnerHasLedgerEntries => SelectedOwnerLedger.Count > 0;

    private int _financeHistoryPage;

    public IReadOnlyList<OwnerLedgerEntry> PagedOwnerLedger
    {
        get
        {
            var max = Preferences?.FinanceHistoryMaxVisible ?? 20;
            return SelectedOwnerLedger.Skip(_financeHistoryPage * max).Take(max).ToList();
        }
    }

    public int FinanceHistoryPageCount
    {
        get
        {
            var max = Preferences?.FinanceHistoryMaxVisible ?? 20;
            return Math.Max(1, (int) Math.Ceiling(SelectedOwnerLedger.Count / (double) max));
        }
    }

    public bool FinanceHistoryHasPrev => _financeHistoryPage > 0;
    public bool FinanceHistoryHasNext => _financeHistoryPage < FinanceHistoryPageCount - 1;
    public string FinanceHistoryPageLabel => $"{_financeHistoryPage + 1} / {FinanceHistoryPageCount}";

    public void FinanceHistoryPrevPage()
    {
        if (!FinanceHistoryHasPrev) return;
        _financeHistoryPage--;
        NotifyPagedLedger();
    }

    public void FinanceHistoryNextPage()
    {
        if (!FinanceHistoryHasNext) return;
        _financeHistoryPage++;
        NotifyPagedLedger();
    }

    private void ResetFinanceHistoryPage()
    {
        _financeHistoryPage = 0;
        NotifyPagedLedger();
    }

    private void NotifyPagedLedger()
    {
        OnPropertyChanged(nameof(PagedOwnerLedger));
        OnPropertyChanged(nameof(FinanceHistoryPageCount));
        OnPropertyChanged(nameof(FinanceHistoryHasPrev));
        OnPropertyChanged(nameof(FinanceHistoryHasNext));
        OnPropertyChanged(nameof(FinanceHistoryPageLabel));
    }

    public double SelectedOwnerGraphHeight
    {
        get => _selectedOwnerGraphHeight;
        set
        {
            var clamped = Math.Clamp(value, 120, 500);
            if (Math.Abs(_selectedOwnerGraphHeight - clamped) < 0.1) return;
            _selectedOwnerGraphHeight = clamped;
            OnPropertyChanged();
        }
    }

    // Chart window steps: null = all, positive = last N days
    // "period" is dynamic — resolved at runtime via SelectedOwnerCurrentPeriodStartDate
    public static readonly IReadOnlyList<int?> ChartWindowSteps = [1, 3, 7, null /* period */, 14, 30, -1 /* all */];
    private const int PeriodStepIndex = 3;

    public int? ChartWindowDays
    {
        get => _chartWindowDays;
        set
        {
            if (_chartWindowDays == value) return;
            _chartWindowDays = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChartWindowLabel));
            OnPropertyChanged(nameof(SelectedOwnerFinanceSeries));
            OnPropertyChanged(nameof(SelectedOwnerFinanceXAxes));
        }
    }

    public string ChartWindowLabel
    {
        get
        {
            if (_chartWindowDays == -1) return "All";
            if (_chartWindowDays == null)
            {
                var start = SelectedOwnerCurrentPeriodStartDate;
                var days = (int)(DateTime.Today - start).TotalDays + 1;
                return $"Period ({days}d)";
            }
            return $"{_chartWindowDays}d";
        }
    }

    private int ResolveWindowDays()
    {
        if (_chartWindowDays == null)
        {
            var start = SelectedOwnerCurrentPeriodStartDate;
            return Math.Max(1, (int)(DateTime.Today - start).TotalDays + 1);
        }
        return _chartWindowDays.Value;
    }

    public void ChartWindowStep(int direction)
    {
        // Map current value to step index
        int currentIdx;
        if (_chartWindowDays == -1) currentIdx = ChartWindowSteps.Count - 1;
        else if (_chartWindowDays == null) currentIdx = PeriodStepIndex;
        else
        {
            currentIdx = ChartWindowSteps.ToList().IndexOf(_chartWindowDays);
            if (currentIdx < 0) currentIdx = direction > 0 ? 0 : ChartWindowSteps.Count - 1;
        }

        var nextIdx = Math.Clamp(currentIdx + direction, 0, ChartWindowSteps.Count - 1);
        ChartWindowDays = ChartWindowSteps[nextIdx];
    }

    private (Dictionary<DateTime, double> earned, Dictionary<DateTime, double> payout, List<DateTime> dates) GetFinanceDateData()
    {
        var name = EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(name))
            return (new Dictionary<DateTime, double>(), new Dictionary<DateTime, double>(), []);

        var profile = GetOrCreateProfile(name);
        var earnedByDay = (profile.CycleHistory ?? new List<OwnerCycleRecord>())
            .GroupBy(c => c.Date.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => g.Sum(c => (double)c.EarnedAmount));
        var payoutByDay = (profile.Withdrawals ?? new List<OwnerWithdrawalEntry>())
            .GroupBy(w => w.Timestamp.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => g.Sum(w => (double)w.Amount));
        var allDates = earnedByDay.Keys.Union(payoutByDay.Keys).OrderBy(d => d).ToList();
        return (earnedByDay, payoutByDay, allDates);
    }

    private List<DateTime> ApplyChartWindow(List<DateTime> allDates)
    {
        if (_chartWindowDays == -1 || allDates.Count == 0) return allDates;
        var windowDays = ResolveWindowDays();
        var cutoff = DateTime.Today.AddDays(-(windowDays - 1));
        return allDates.Where(d => d >= cutoff).ToList();
    }

    public ISeries[] SelectedOwnerFinanceSeries
    {
        get
        {
            var (earnedByDay, payoutByDay, allDates) = GetFinanceDateData();
            if (allDates.Count == 0)
                return [new ColumnSeries<ObservablePoint> { Name = "Earned", Values = [new ObservablePoint(0, 0)] }];

            var dates = ApplyChartWindow(allDates);
            if (dates.Count == 0)
                return [new ColumnSeries<ObservablePoint> { Name = "Earned", Values = [new ObservablePoint(0, 0)] }];

            // Running balance starts at opening balance + all transactions before the window
            var running = (double)SelectedOwnerOpeningBalance;
            foreach (var d in allDates.Where(d => d < dates[0]))
            {
                running += (earnedByDay.TryGetValue(d, out var e) ? e : 0)
                         - (payoutByDay.TryGetValue(d, out var p) ? p : 0);
            }

            var earnedPts = new List<ObservablePoint>();
            var payoutPts = new List<ObservablePoint>();
            var balancePts = new List<ObservablePoint>();
            for (var i = 0; i < dates.Count; i++)
            {
                var day = dates[i];
                var e = earnedByDay.TryGetValue(day, out var ev) ? ev : 0;
                var p = payoutByDay.TryGetValue(day, out var pv) ? pv : 0;
                running += e - p;
                if (e != 0) earnedPts.Add(new ObservablePoint(i, e));
                if (p != 0) payoutPts.Add(new ObservablePoint(i, -p));
                balancePts.Add(new ObservablePoint(i, running));
            }
            return
            [
                new ColumnSeries<ObservablePoint>
                {
                    Name = "Earned", Values = earnedPts,
                    Fill = new SolidColorPaint { Color = new SKColor(34, 197, 94, 220) },
                    Stroke = new SolidColorPaint { Color = new SKColor(16, 185, 129, 200) }
                },
                new ColumnSeries<ObservablePoint>
                {
                    Name = "Payout", Values = payoutPts,
                    Fill = new SolidColorPaint { Color = new SKColor(255, 193, 7, 220) },
                    Stroke = new SolidColorPaint { Color = new SKColor(204, 160, 13, 200) }
                },
                new LineSeries<ObservablePoint>
                {
                    Name = "Balance", Values = balancePts,
                    Stroke = null, GeometrySize = 10,
                    GeometryStroke = new SolidColorPaint { Color = SKColors.White },
                    GeometryFill = new SolidColorPaint { Color = new SKColor(14, 165, 233, 255) }
                }
            ];
        }
    }

    public Axis[] SelectedOwnerFinanceXAxes
    {
        get
        {
            var (_, _, allDates) = GetFinanceDateData();
            if (allDates.Count == 0) return [DarkXAxis(["—"])];
            var dates = ApplyChartWindow(allDates);
            var labels = dates.Select(d => d.ToString("dd MMM")).ToArray();
            return [DarkXAxis(labels.Length > 0 ? labels : ["—"])];
        }
    }

    public Axis[] SelectedOwnerFinanceYAxes =>
    [
        new Axis
        {
            LabelsPaint = new SolidColorPaint(new SKColor(160, 160, 160)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(60, 60, 60)),
            SubseparatorsPaint = null,
        }
    ];

    private static Axis DarkXAxis(string[] labels) => new Axis
    {
        Labels = labels,
        LabelsPaint = new SolidColorPaint(new SKColor(160, 160, 160)),
        SeparatorsPaint = new SolidColorPaint(new SKColor(60, 60, 60)),
        SubseparatorsPaint = null,
    };

    // --- Islands Summary (selected owner aggregate) ---

    public IReadOnlyList<OwnerIslandSummaryRow> IslandSummaryRows
    {
        get
        {
            var controller = GetController();
            if (controller == null) return [];

            var owners = GetEffectiveOwnerNames().ToList();
            if (owners.Count == 0) return [];

            var rows = new List<OwnerIslandSummaryRow>();
            foreach (var owner in owners)
            {
                var ownerIslands = controller.GetIslandsByOwner(owner).ToList();

                var laborerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var totalLaborers = 0;

                // Plot type totals: type → total slot/quantity count
                var plotTypeTotals = new Dictionary<PlotType, int>();
                // Farmable content counts: (PlotType, display name) → count
                var farmableContents = new Dictionary<(PlotType, string), int>();

                foreach (var island in ownerIslands)
                {
                    foreach (var plot in island.Plots)
                    {
                        if (plot.PlotType == PlotType.House)
                        {
                            var cfg = LaborerConfigHelper.ParseConfiguration(plot.Configuration);
                            for (var slot = 1; slot <= 3; slot++)
                            {
                                if (!cfg.TryGetValue(LaborerConfigHelper.LaborerKey(slot), out var rawType)
                                    || string.IsNullOrWhiteSpace(rawType)
                                    || string.Equals(rawType, LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                cfg.TryGetValue(LaborerConfigHelper.JournalTierKey(slot), out var tierText);
                                var digits = new string((tierText ?? string.Empty).Where(char.IsDigit).ToArray());
                                var tierPart = string.IsNullOrWhiteSpace(digits) ? string.Empty : $"T{digits} ";
                                var typePart = LaborerConfigHelper.ToDisplayLaborerType(rawType);
                                var key = $"{tierPart}{typePart}";

                                laborerCounts[key] = (laborerCounts.TryGetValue(key, out var c) ? c : 0) + 1;
                                totalLaborers++;
                            }

                            plotTypeTotals[PlotType.House] = (plotTypeTotals.TryGetValue(PlotType.House, out var hc) ? hc : 0) + plot.Quantity;
                        }
                        else
                        {
                            var qty = Math.Max(plot.Quantity, 1);
                            plotTypeTotals[plot.PlotType] = (plotTypeTotals.TryGetValue(plot.PlotType, out var pc) ? pc : 0) + qty;

                            if (plot.PlotType.HasFarmableConfig())
                            {
                                var contents = PlotTypeExtensions.ParseConfiguredObjectCounts(plot.PlotType, plot.Configuration, qty);
                                foreach (var kv in contents)
                                {
                                    var contentKey = (plot.PlotType, kv.Key);
                                    farmableContents[contentKey] = (farmableContents.TryGetValue(contentKey, out var fc) ? fc : 0) + kv.Value;
                                }
                            }
                        }
                    }
                }

                var laborerBreakdown = laborerCounts
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new LaborerTypeCount { Display = kv.Key, Count = kv.Value })
                    .ToList();

                var plotBreakdown = new List<PlotTypeSummaryRow>();

                if (totalLaborers > 0 || plotTypeTotals.ContainsKey(PlotType.House))
                {
                    plotBreakdown.Add(new PlotTypeSummaryRow
                    {
                        DisplayName = "House",
                        TotalCount = plotTypeTotals.TryGetValue(PlotType.House, out var hTotal) ? hTotal : 0,
                        Details = laborerBreakdown
                    });
                }

                foreach (var pt in plotTypeTotals.Keys.Where(k => k != PlotType.House).OrderBy(k => k.GetDisplayName()))
                {
                    var details = farmableContents
                        .Where(kv => kv.Key.Item1 == pt)
                        .OrderBy(kv => kv.Key.Item2, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => new LaborerTypeCount { Display = kv.Key.Item2, Count = kv.Value })
                        .ToList();

                    plotBreakdown.Add(new PlotTypeSummaryRow
                    {
                        DisplayName = pt.GetDisplayName(),
                        TotalCount = plotTypeTotals[pt],
                        Details = details
                    });
                }

                rows.Add(new OwnerIslandSummaryRow
                {
                    OwnerName = owner,
                    IslandCount = ownerIslands.Count,
                    TotalLaborers = totalLaborers,
                    LaborersByTierType = laborerBreakdown,
                    PlotBreakdown = plotBreakdown
                });
            }
            return rows;
        }
    }

    // --- Cycle form ---
    public static IEnumerable<CycleRecordType> CycleRecordTypes => Enum.GetValues<CycleRecordType>();

    public CycleRecordType NewCycleType
    {
        get => _newCycleType;
        set
        {
            _newCycleType = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NewCycleShowIslandCount));
        }
    }

    public bool NewCycleShowIslandCount => _newCycleType == CycleRecordType.Islands;

    public string NewCycleIslandCount { get => _newCycleIslandCount; set { _newCycleIslandCount = value; OnPropertyChanged(); } }
    public string NewCycleAmount
    {
        get => _newCycleAmount;
        set
        {
            _newCycleAmount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NewCycleAmountPreview));
            OnPropertyChanged(nameof(CanRecordCycle));
        }
    }
    public string NewCycleNote { get => _newCycleNote; set { _newCycleNote = value; OnPropertyChanged(); } }
    public DateTime NewCycleDate { get => _newCycleDate; set { _newCycleDate = value; OnPropertyChanged(); } }
    public bool CanRecordCycle => EvaluateSilverExpression(NewCycleAmount) is { } ca && ca > 0 && HasSelectedOwner;
    public string NewCycleAmountPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NewCycleAmount)) return string.Empty;
            var v = EvaluateSilverExpression(NewCycleAmount);
            return v.HasValue ? $"= {v.Value:N0}" : "?";
        }
    }

    // --- Withdrawal form ---
    public string NewWithdrawalAmount
    {
        get => _newWithdrawalAmount;
        set
        {
            _newWithdrawalAmount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NewWithdrawalAmountPreview));
            OnPropertyChanged(nameof(CanRecordWithdrawal));
        }
    }
    public string NewWithdrawalNote { get => _newWithdrawalNote; set { _newWithdrawalNote = value; OnPropertyChanged(); } }
    public DateTime NewWithdrawalDate { get => _newWithdrawalDate; set { _newWithdrawalDate = value; OnPropertyChanged(); } }
    public DateTime NewWithdrawalPaidForWeekStart
    {
        get => _newWithdrawalPaidForWeekStart ?? GetCurrentCalendarWeekStart();
        set { _newWithdrawalPaidForWeekStart = value; OnPropertyChanged(); }
    }
    public bool CanRecordWithdrawal => EvaluateSilverExpression(NewWithdrawalAmount) is { } wa && wa > 0 && HasSelectedOwner;
    public string NewWithdrawalAmountPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(NewWithdrawalAmount)) return string.Empty;
            var v = EvaluateSilverExpression(NewWithdrawalAmount);
            return v.HasValue ? $"= {v.Value:N0}" : "?";
        }
    }

    internal static decimal? EvaluateSilverExpression(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        // Normalize: lowercase, replace 'x' with '*', strip spaces
        var expr = input.Trim().ToLowerInvariant().Replace('x', '*').Replace(" ", "");
        // Expand k/m suffixes before operators so "180k*5" works too
        expr = System.Text.RegularExpressions.Regex.Replace(expr, @"(\d+(?:\.\d+)?)k", m =>
            (decimal.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 1000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        expr = System.Text.RegularExpressions.Regex.Replace(expr, @"(\d+(?:\.\d+)?)m", m =>
            (decimal.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 1_000_000m).ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var result = EvalArithmetic(expr);
            return result is >= 0 ? result : null;
        }
        catch { return null; }
    }

    private static decimal? EvalArithmetic(string expr)
    {
        // Recursive descent: handles +, -, *, /
        expr = expr.Trim();
        // Addition / subtraction (lowest precedence, left-to-right)
        int i = expr.Length - 1;
        int depth = 0;
        while (i >= 0)
        {
            char c = expr[i];
            if (c == ')') depth++;
            else if (c == '(') depth--;
            else if (depth == 0 && (c == '+' || c == '-') && i > 0)
            {
                var left = EvalArithmetic(expr[..i]);
                var right = EvalArithmetic(expr[(i + 1)..]);
                if (left == null || right == null) return null;
                return c == '+' ? left + right : left - right;
            }
            i--;
        }
        // Multiplication / division
        i = expr.Length - 1;
        depth = 0;
        while (i >= 0)
        {
            char c = expr[i];
            if (c == ')') depth++;
            else if (c == '(') depth--;
            else if (depth == 0 && (c == '*' || c == '/') && i > 0)
            {
                var left = EvalArithmetic(expr[..i]);
                var right = EvalArithmetic(expr[(i + 1)..]);
                if (left == null || right == null) return null;
                if (c == '/' && right == 0) return null;
                return c == '*' ? left * right : left / right;
            }
            i--;
        }
        // Parentheses
        if (expr.StartsWith('(') && expr.EndsWith(')'))
            return EvalArithmetic(expr[1..^1]);
        // Leaf: decimal literal
        return decimal.TryParse(expr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public void QuickFillTodayCycles()
    {
        NewCycleDate = DateTime.Today;
        NewCycleIslandCount = SelectedOwnerTodayLiveCycledCount.ToString();
        NewCycleAmount = SelectedOwnerTodayLiveEstimate.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        NewCycleNote = string.Empty;
    }

    public bool TryRecordCycle(out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!HasSelectedOwner) { errorMessage = "No owner selected."; return false; }
        var amount = EvaluateSilverExpression(NewCycleAmount);
        if (amount == null || amount <= 0)
        { errorMessage = "Enter a valid earned amount greater than zero."; return false; }
        _ = int.TryParse(NewCycleIslandCount, out var islandCount);
        GetOrCreateProfile(EffectiveOwnerName).CycleHistory.Add(new OwnerCycleRecord
        {
            Date = DateTime.SpecifyKind(NewCycleDate.Date, DateTimeKind.Local),
            RecordType = NewCycleType,
            IslandCount = NewCycleType == CycleRecordType.Islands ? islandCount : 0,
            EarnedAmount = amount.Value,
            Notes = NewCycleNote?.Trim() ?? string.Empty
        });
        SaveOwnerProfile();
        NewCycleAmount = string.Empty;
        NewCycleIslandCount = string.Empty;
        NewCycleNote = string.Empty;
        NewCycleType = CycleRecordType.Islands;
        RefreshOwnerOverview();
        return true;
    }

    public bool TryRecordWithdrawal(out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!HasSelectedOwner) { errorMessage = "No owner selected."; return false; }
        var amount = EvaluateSilverExpression(NewWithdrawalAmount);
        if (amount == null || amount <= 0)
        { errorMessage = "Enter a valid withdrawal amount greater than zero."; return false; }
        GetOrCreateProfile(EffectiveOwnerName).Withdrawals.Add(new OwnerWithdrawalEntry
        {
            Timestamp = DateTime.SpecifyKind(NewWithdrawalDate.Date, DateTimeKind.Local),
            Amount = amount.Value,
            Notes = NewWithdrawalNote?.Trim() ?? string.Empty,
            PaidForWeekStart = NewWithdrawalPaidForWeekStart.Date
        });
        SaveOwnerProfile();
        NewWithdrawalAmount = string.Empty;
        NewWithdrawalNote = string.Empty;
        _newWithdrawalPaidForWeekStart = null;
        OnPropertyChanged(nameof(NewWithdrawalPaidForWeekStart));
        RefreshOwnerOverview();
        return true;
    }

    public void DeleteLedgerEntry(Guid entryId, bool isCycle)
    {
        var name = EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(name)) return;
        var profile = GetOrCreateProfile(name);
        if (isCycle)
        {
            var item = profile.CycleHistory?.FirstOrDefault(c => c.Id == entryId);
            if (item != null) profile.CycleHistory.Remove(item);
        }
        else
        {
            var item = profile.Withdrawals?.FirstOrDefault(w => w.Id == entryId);
            if (item != null) profile.Withdrawals.Remove(item);
        }
        SaveOwnerProfile();
        RefreshOwnerOverview();
    }

    public void UpdateLedgerCycleEntry(OwnerCycleRecord updated)
    {
        var name = EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(name)) return;
        var controller = GetController();
        controller?.UpdateCycleRecord(name, updated);
        RefreshOwnerOverview();
    }

    public OwnerCycleRecord GetCycleRecord(Guid entryId)
    {
        var name = EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return GetOrCreateProfile(name).CycleHistory?.FirstOrDefault(c => c.Id == entryId);
    }

    public OwnerWithdrawalEntry GetWithdrawalRecord(Guid entryId)
    {
        var name = EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(name)) return null;
        return GetOrCreateProfile(name).Withdrawals?.FirstOrDefault(w => w.Id == entryId);
    }

    public void UpdateLedgerWithdrawalEntry(OwnerWithdrawalEntry updated)
    {
        var name = EffectiveOwnerName;
        if (string.IsNullOrWhiteSpace(name)) return;
        var controller = GetController();
        controller?.UpdateWithdrawalEntry(name, updated);
        RefreshOwnerOverview();
    }

    public void RefreshOwnerProfile()
    {
        RefreshOwnerOverview();
    }

    public void RecordOwnerPayment(decimal amount, string notes)
    {
        NewWithdrawalAmount = amount.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        NewWithdrawalNote = notes;
        TryRecordWithdrawal(out _);
    }

    public void SaveOwnerProfile()
    {
        var controller = GetController();
        if (controller == null) return;
        _ = controller.SaveOwnerProfilesAsync();
    }

    public void RefreshOwnerOverview()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastOwnerRefreshUtc) < TimeSpan.FromMilliseconds(300)) return;
        _lastOwnerRefreshUtc = now;

        OnPropertyChanged(nameof(OwnerOptions));
        OnPropertyChanged(nameof(HasSelectedOwner));
        OnPropertyChanged(nameof(SelectedOwnerIslandCount));
        OnPropertyChanged(nameof(SelectedOwnerDoneTodayCount));
        OnPropertyChanged(nameof(SelectedOwnerLeftTodayCount));
        OnPropertyChanged(nameof(GlobalIslandsDoneTodayCount));
        OnPropertyChanged(nameof(GlobalIslandsLeftTodayCount));
        OnPropertyChanged(nameof(GlobalHandlingTimeStatusText));
        OnPropertyChanged(nameof(SelectedOwnerTotalCycleEarned));
        OnPropertyChanged(nameof(SelectedOwnerTotalWithdrawn));
        OnPropertyChanged(nameof(SelectedOwnerOpeningBalance));
        OnPropertyChanged(nameof(SelectedOwnerBalance));
        OnPropertyChanged(nameof(SelectedOwnerDailyPotentialPay));
        OnPropertyChanged(nameof(SelectedOwnerTodayLiveEstimate));
        OnPropertyChanged(nameof(SelectedOwnerTodayLiveCycledCount));
        OnPropertyChanged(nameof(SelectedOwnerCanQuickFill));
        OnPropertyChanged(nameof(SelectedOwnerNextPayoutDate));
        OnPropertyChanged(nameof(SelectedOwnerCurrentPeriodStartDate));
        OnPropertyChanged(nameof(SelectedOwnerPayoutScheduleText));
        OnPropertyChanged(nameof(SelectedOwnerCurrentPeriodRecordedEarned));
        OnPropertyChanged(nameof(SelectedOwnerDaysUntilPayout));
        OnPropertyChanged(nameof(SelectedOwnerProjectedAgreementPayout));
        OnPropertyChanged(nameof(SelectedOwnerProjectedWeeklyIncome));
        OnPropertyChanged(nameof(SelectedOwnerUnpaidPreviousEarned));
        OnPropertyChanged(nameof(HasUnpaidPreviousEarned));
        OnPropertyChanged(nameof(SelectedOwnerTodayExtraEarned));
        OnPropertyChanged(nameof(SelectedOwnerTodayExtraNotes));
        OnPropertyChanged(nameof(SelectedOwnerProfile));
        OnPropertyChanged(nameof(SelectedOwnerLedger));
        OnPropertyChanged(nameof(SelectedOwnerHasLedgerEntries));
        ResetFinanceHistoryPage();
        OnPropertyChanged(nameof(ChartWindowLabel));
        OnPropertyChanged(nameof(SelectedOwnerFinanceSeries));
        OnPropertyChanged(nameof(SelectedOwnerFinanceXAxes));
        OnPropertyChanged(nameof(SelectedOwnerFinanceYAxes));
        OnPropertyChanged(nameof(SelectedOwnerDefaultPayPerIsland));
        OnPropertyChanged(nameof(SelectedOwnerPayoutDay));
        OnPropertyChanged(nameof(SelectedOwnerEngagementType));
        OnPropertyChanged(nameof(IsPaidManagerEngagement));
        OnPropertyChanged(nameof(SelectedOwnerNotes));
        OnPropertyChanged(nameof(SelectedOwnerWebhookUrl));
        OnPropertyChanged(nameof(SelectedOwnerTodayRecordedEarned));
        OnPropertyChanged(nameof(SelectedOwnerTodayRecordedIslandCount));
        OnPropertyChanged(nameof(AllIslandsDoneToday));
        OnPropertyChanged(nameof(DiscordEmbedPreview));
        OnPropertyChanged(nameof(CanRecordCycle));
        OnPropertyChanged(nameof(CanRecordWithdrawal));
        OnPropertyChanged(nameof(IslandSummaryRows));
        RefreshManagerResponsibilityItems();
        RefreshOwnerYield();
    }

    // ── Island / plot CRUD (delegated from view code-behind) ──────────────────

    public IslandEntry BuildAddIslandPrefill()
    {
        var controller = GetController();
        var suggestion = controller?.BuildSessionSuggestion();
        if (suggestion == null) return null;

        var cityAlreadyUsed = !string.IsNullOrWhiteSpace(suggestion.City)
            && (controller?.IslandExists(suggestion.IslandName, suggestion.City) ?? false);

        return new IslandEntry
        {
            Name = suggestion.IslandName,
            Tier = suggestion.Tier > 0 ? suggestion.Tier : 6,
            TierDisplay = $"T{(suggestion.Tier > 0 ? suggestion.Tier : 6)}",
            CityFaction = cityAlreadyUsed ? CityFaction.Unknown : IslandMapping.ParseCityFaction(suggestion.City),
            CityName = cityAlreadyUsed ? string.Empty : suggestion.City,
            OwnerName = suggestion.Owner,
            HasPremium = suggestion.HasPremium
        };
    }

    public void CommitAddIsland(IslandEntry result)
    {
        if (result == null) return;
        var controller = GetController();
        if (controller == null) return;
        var newId = controller.AddIsland(IslandMapping.NewIslandFromEntry(result));
        if (newId.HasValue)
            controller.SelectIslandById(newId.Value);
    }

    public void CommitEditIsland(IslandEntry entry, IslandEntry result, bool deleteRequested)
    {
        if (entry == null) return;
        var controller = GetController();
        if (controller == null) return;

        if (deleteRequested)
        {
            controller.RemoveIsland(entry.IslandId);
            return;
        }

        if (result == null) return;
        var island = controller.GetById(entry.IslandId);
        if (island == null) return;
        IslandMapping.ApplyEntryToIsland(result, island);
        controller.UpdateIsland(island);
    }

    public void CommitAddPlot(Guid islandId, IslandPlot result)
    {
        if (result == null) return;
        var controller = GetController();
        if (controller == null) return;
        var island = controller.GetById(islandId);
        if (island == null) return;
        island.AddPlot(result);
        controller.UpdateIsland(island);
    }

    public void CommitEditPlot(Guid islandId, IslandPlot result)
    {
        if (result == null) return;
        var controller = GetController();
        if (controller == null) return;
        controller.UpdateIsland(controller.GetById(islandId));
    }

    public void CommitDeletePlot(Guid islandId, Guid plotId)
    {
        var controller = GetController();
        if (controller == null) return;
        var island = controller.GetById(islandId);
        if (island == null) return;
        var plot = island.Plots.FirstOrDefault(p => p.Id == plotId);
        if (plot == null) return;
        island.RemovePlot(plot);
        SelectedPlot = null;
        controller.UpdateIsland(island);
    }

    public void CommitPlantAll(Guid islandId)
    {
        var controller = GetController();
        if (controller == null) return;
        var island = controller.GetById(islandId);
        if (island == null) return;
        island.PlantAll();
        controller.UpdateIsland(island);
    }
}
