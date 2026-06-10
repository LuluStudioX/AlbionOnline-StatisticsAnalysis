using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Network.Events;
using System;

namespace StatisticsAnalysisTool.Island;

public class LaborerSnapshot
{
    // Runtime object ID shared across NewBuilding, LaborerObjectInfo and LaborerObjectJobInfo.
    public long ObjectId { get; }

    // Sequence number assigned when this snapshot was first created from a NewBuilding event.
    // Laborers in the same physical house are typically broadcast consecutively by the server,
    // so detection order is a more reliable grouping proxy than ObjectId when HousePlotGuid is absent.
    public long DetectionOrder { get; set; }

    public Guid BuildingGuid { get; set; }

    // GUID of the parent island plot (the physical house). Shared by all laborers in the same house.
    // Populated from NewBuilding param 9. Used to correctly group laborers per house.
    public Guid HousePlotGuid { get; set; }

    public string UniqueBuildingName { get; set; } = string.Empty;

    public int BuildingTier { get; set; }

    public string LaborerType { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;

    public string UniqueName { get; set; } = string.Empty;

    public string FullName => $"{FirstName} {LastName}".Trim();

    public bool HasPremium { get; set; }
    public int Nutrition { get; set; }

    // Latched: the laborer has been dispatched this session and not yet confirmed collected. Param 8 /
    // the job-info journal id are sent intermittently while the laborer is out, so a single packet that
    // omits them must not clear this. Reset on island change.
    public bool HasActiveJob { get; set; }

    // On-job / loot-ready are DERIVED from the latched job state + the return time, never from a single
    // packet flag. On-job = dispatched with the return still in the future; loot-ready = return passed;
    // home = no active job. Keeping IsOnJob derived (not a sticky bool) is what stops "all on job" from
    // becoming trivially true and firing the owner webhook mid-collection.
    public bool IsOnJob => HasActiveJob && ReadyAtUtc.HasValue && ReadyAtUtc.Value > DateTime.UtcNow;
    public bool IsLootReady => HasActiveJob && ReadyAtUtc.HasValue && ReadyAtUtc.Value <= DateTime.UtcNow
        && DateTime.UtcNow < ReadyAtUtc.Value.AddHours(IslandConstants.LaborerLootExpiryHours);

    // Return/ready-at time. Both wire timestamps are job COMPLETION times, not starts:
    //   param 8 (LaborerObjectInfo) = future completion, present only while the laborer is away;
    //   param 5 (LaborerObjectJobInfo, stored as JobStartTime) = the completion of the job the laborer is
    //   back from — it matches the in-game "rewards expire in N days" deadline (7d after completion).
    // A laborer back with uncollected loot sends only param 5 (recent past) and no param 8, so ReadyAtUtc
    // is in the past => loot-ready. (Adding a cycle here was the bug: it projected param 5 ~22h into the
    // future and mislabeled a back/loot-ready laborer as on-job for ~17h.)
    public DateTime? ReadyAtUtc => JobDispatchTime ?? JobStartTime;

    // True once this laborer has been observed at home (IsOnJob=false) at least once.
    // Used to distinguish "already on job when we arrived" from "just dispatched".
    public bool HasBeenSeenAsHome { get; private set; }

    public Guid? ActiveJobId { get; set; }
    public DateTime? JobDispatchTime { get; set; }

    // Elapsed time since dispatch, computed once when the laborer is first observed on-job during this visit.
    // Stays frozen so the Live Status display doesn't tick up every 60 s.
    public string SentDetailSnapshot { get; set; }

    public string SentByCharacter { get; set; } = string.Empty;

    // World position from NewBuilding param 4. Used to match laborer house to map slot via coordinate transform.
    public (float X, float Y)? WorldPosition { get; set; }

    public FixPoint CurrentFameFill { get; set; }

    public int FameFillValue { get; set; }
    public int Happiness { get; set; }
    public DateTime? JobStartTime { get; set; }

    public int JournalItemId { get; set; }

    public LaborerSnapshot(long objectId)
    {
        ObjectId = objectId;
    }

    public void UpdateFromNewBuilding(NewBuildingEvent e)
    {
        BuildingGuid = e.BuildingGuid;
        if (e.HousePlotGuid != Guid.Empty)
            HousePlotGuid = e.HousePlotGuid;
        UniqueBuildingName = e.UniqueName;
        Nutrition = e.Nutrition;
        HasPremium = e.HasPremium;
        UniqueName = e.UniqueName;
        FirstName = e.LaborerFirstName;
        LastName = e.LaborerLastName;
        if (!string.IsNullOrWhiteSpace(e.IslandOwnerName))
            SentByCharacter = e.IslandOwnerName;
        if (e.Position.HasValue)
            WorldPosition = e.Position.Value;
        var (parsedTier, parsedType) = ParseUniqueName(e.UniqueName);
        if (parsedTier > 0)
        {
            if (BuildingTier == 0 || parsedTier > BuildingTier)
            {
                BuildingTier = parsedTier;
                if (!string.IsNullOrEmpty(parsedType))
                    LaborerType = parsedType;
            }
            else if (string.IsNullOrEmpty(LaborerType) && !string.IsNullOrEmpty(parsedType))
            {
                LaborerType = parsedType;
            }
        }
    }

    public bool UpdateFromLaborerObjectInfo(LaborerObjectInfoEvent e)
    {
        var prevOnJob = IsOnJob;
        var prevLootReady = IsLootReady;
        var prevFirstName = FirstName;
        var prevLastName = LastName;

        if (!string.IsNullOrEmpty(e.FirstName)) FirstName = e.FirstName;
        if (!string.IsNullOrEmpty(e.LastName)) LastName = e.LastName;
        // Job state is latched (see HasActiveJob): only set, never clear from a packet that omits param 8.
        if (e.IsOnJob) HasActiveJob = true;
        ActiveJobId = e.ActiveJobId;
        // Latch the return time too — keep the last known value when this packet omits param 8.
        if (e.JobDispatchTime.HasValue) JobDispatchTime = e.JobDispatchTime;
        SentByCharacter = e.SentByCharacter;
        FameFillValue = (int)e.FameFill.DoubleValue;
        Happiness = e.Happiness;

        if (!e.IsOnJob)
        {
            HasBeenSeenAsHome = true;
        }

        if (IsOnJob && !IsLootReady && string.IsNullOrEmpty(SentDetailSnapshot) && JobDispatchTime.HasValue)
            SentDetailSnapshot = FormatSentElapsed(DateTime.UtcNow, JobDispatchTime.Value.AddHours(-IslandConstants.LaborerBaseCycleHours));

        return IsOnJob != prevOnJob
            || IsLootReady != prevLootReady
            || FirstName != prevFirstName
            || LastName != prevLastName;
    }

    public void UpdateFromJobInfo(LaborerObjectJobInfoEvent e)
    {
        JournalItemId = e.JournalItemId;
        CurrentFameFill = e.CurrentFameFill;
        if (e.JobStartTime.HasValue) JobStartTime = e.JobStartTime;

        // Form A (non-zero journal id) latches the active job. This is the primary detection path after
        // a game reconnect, when LaborerObjectInfo param 8 may be absent. The bare form carries no state
        // and must not clear it (it is broadcast even while the laborer is still out). On-job/loot-ready
        // are derived from ReadyAtUtc, not from this event.
        if (e.IsAwayOnJob)
        {
            HasActiveJob = true;
            if (string.IsNullOrEmpty(SentDetailSnapshot) && JobDispatchTime.HasValue)
                SentDetailSnapshot = FormatSentElapsed(DateTime.UtcNow, JobDispatchTime.Value.AddHours(-IslandConstants.LaborerBaseCycleHours));
        }
    }

    public void TrySetTypeFromJournal(string journalUniqueName)
    {
        if (string.IsNullOrEmpty(journalUniqueName)) return;

        var parts = journalUniqueName.Split('_');
        if (parts.Length < 3) return;
        if (!string.Equals(parts[1], "JOURNAL", StringComparison.OrdinalIgnoreCase)) return;

        var tierStr = parts[0];
        if (tierStr.Length < 2 || tierStr[0] != 'T') return;
        if (!int.TryParse(tierStr[1..], out var tier)) return;

        var type = string.Join('_', parts[2..]);

        if (BuildingTier == 0 || tier > BuildingTier)
        {
            BuildingTier = tier;
            LaborerType = type;
        }
        else if (string.IsNullOrEmpty(LaborerType))
        {
            LaborerType = type;
        }
    }

    public static string FormatSentElapsed(DateTime now, DateTime dispatchTime)
    {
        var elapsed = now - dispatchTime;
        if (elapsed.TotalSeconds < 0) return "just now";
        return elapsed.TotalHours >= 1
            ? $"Sent {(int)elapsed.TotalHours}h {elapsed.Minutes}m ago"
            : $"Sent {elapsed.Minutes}m ago";
    }

    private static (int tier, string type) ParseUniqueName(string uniqueName)
    {
        if (string.IsNullOrEmpty(uniqueName)) return (0, string.Empty);

        var parts = uniqueName.Split('_');
        if (parts.Length < 3) return (0, string.Empty);

        var tierStr = parts[0];
        if (tierStr.Length < 2 || tierStr[0] != 'T') return (0, string.Empty);

        if (!int.TryParse(tierStr[1..], out var tier)) return (0, string.Empty);

        var type = string.Join('_', parts[2..]);
        return (tier, type);
    }
}
