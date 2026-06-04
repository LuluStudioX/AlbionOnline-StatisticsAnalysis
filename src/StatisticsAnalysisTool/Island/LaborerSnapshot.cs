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

    // True when param 8/9 are present in LaborerObjectInfo (laborer dispatched on job).
    public bool IsOnJob { get; set; }

    // True when LaborerObjectJobInfo param 1 = true (home with loot available).
    public bool IsLootReady { get; set; }

    // True once this laborer has been observed at home (IsOnJob=false) at least once.
    // Used to distinguish "already on job when we arrived" from "just dispatched".
    public bool HasBeenSeenAsHome { get; private set; }

    // Set when the laborer transitions from home → dispatched during the current visit.
    // Cleared automatically when the laborer returns home.
    public DateTime? JustSentAt { get; set; }

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
    public DateTime? NextReturnAt { get; set; }
    public DateTime? LastJobStartedAt { get; set; }
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
        // Don't overwrite IsLootReady=true with IsOnJob=true from a stale dispatch ticks param.
        // LaborerObjectJobInfo is authoritative for loot-ready state.
        if (!(IsLootReady && e.IsOnJob))
            IsOnJob = e.IsOnJob;
        ActiveJobId = e.ActiveJobId;
        JobDispatchTime = e.JobDispatchTime;
        SentByCharacter = e.SentByCharacter;
        FameFillValue = (int)e.FameFill.DoubleValue;
        Happiness = e.Happiness;
        if (e.NextReturnAt.HasValue) NextReturnAt = e.NextReturnAt;
        if (e.LastJobStartedAt.HasValue) LastJobStartedAt = e.LastJobStartedAt;

        if (!e.IsOnJob)
        {
            HasBeenSeenAsHome = true;
            JustSentAt = null;
            SentDetailSnapshot = string.Empty;
        }

        if (e.IsOnJob && !IsLootReady)
        {
            if (string.IsNullOrEmpty(SentDetailSnapshot) && JobDispatchTime.HasValue)
                SentDetailSnapshot = FormatSentElapsed(DateTime.UtcNow, JobDispatchTime.Value);
        }

        return IsOnJob != prevOnJob
            || IsLootReady != prevLootReady
            || FirstName != prevFirstName
            || LastName != prevLastName;
    }

    public void UpdateFromJobInfo(LaborerObjectJobInfoEvent e)
    {
        IsLootReady = e.IsLootReady;
        JournalItemId = e.JournalItemId;
        CurrentFameFill = e.CurrentFameFill;
        if (e.JobStartTime.HasValue) JobStartTime = e.JobStartTime;

        if (e.IsAwayOnJob)
        {
            // Laborer is actively away — journal present but loot not yet ready.
            // This is the primary detection path after a game session reconnect, because
            // LaborerObjectInfo param 8 (dispatch ticks) is only present in the same session
            // the laborer was dispatched. On subsequent visits, only LaborerObjectJobInfo
            // reliably signals the away-on-job state via a non-zero JournalItemId.
            IsOnJob = true;
            if (string.IsNullOrEmpty(SentDetailSnapshot) && JobDispatchTime.HasValue)
                SentDetailSnapshot = FormatSentElapsed(DateTime.UtcNow, JobDispatchTime.Value);
        }
        else if (e.IsLootReady)
        {
            IsOnJob = false;
            SentDetailSnapshot = string.Empty;
        }
        else
        {
            IsOnJob = false;
            SentDetailSnapshot = string.Empty;
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
