using FluentAssertions;
using NUnit.Framework;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Network;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StatisticsAnalysisTool.UnitTests.IslandManagement;

// G10/G11: every island handler must bind the documented game wire code. Several handlers bind via
// (int)EventCodes.X, which only works while the enum ordinal equals the wire code — assert that here so
// inserting an enum member above code 201 (which would shift the ordinals) fails loudly.
[TestFixture]
public class IslandHandlerWireCodeTests
{
    // These handlers bind via (int)EventCodes.X, which only works because the enum ordinal happens to equal
    // the wire code. Inserting an enum member above any of them shifts the ordinals and silently breaks the
    // binding — this guard fails loudly if that happens.
    [TestCase(EventCodes.NewJournalItem, 35)]
    [TestCase(EventCodes.NewBuilding, 45)]
    [TestCase(EventCodes.LaborerObjectInfo, 56)]
    [TestCase(EventCodes.LaborerObjectJobInfo, 57)]
    [TestCase(EventCodes.FarmableObjectInfo, 201)]
    public void OrdinalBoundEventCode_OrdinalEqualsWireCode(EventCodes code, int expectedWireCode)
    {
        ((int) code).Should().Be(expectedWireCode,
            "this handler binds (int)EventCodes.{0}; if the ordinal drifts from the wire code the handler silently stops receiving packets", code);
    }

    // These two handlers DO NOT trust the ordinal — they hardcode the wire literal in their base(...) ctor
    // precisely because the enum ordinal differs from the wire code. Document that divergence so nobody
    // "fixes" the handlers to use (int)EventCodes.X (which would bind the wrong code).
    [TestCase(EventCodes.NewLaborerItem, 32, 36)]
    [TestCase(EventCodes.ActionOnBuildingFinished, 60, 66)]
    public void LiteralBoundEventCode_OrdinalDiffersFromWireCode(EventCodes code, int wireCode, int ordinal)
    {
        ((int) code).Should().Be(ordinal, "the enum ordinal is the value the (int) cast would yield");
        ordinal.Should().NotBe(wireCode, "the handler must hardcode the wire literal {0}, not bind (int)EventCodes.{1} (= {2})", wireCode, code, ordinal);
    }
}

// Island accounting follows the Albion game day, which rolls at UTC midnight. A cycle finished after the
// player's LOCAL midnight but before UTC midnight still belongs to the previous UTC day in-game. Records
// are stamped on IslandTime.Today (UTC) and the "Done today" counters filter by the same basis; bucketing
// the counter by local day instead is what showed 0/24 right after a late-night session.
[TestFixture]
public class IslandAccountingDayTests
{
    [Test]
    public void IslandTime_Today_IsUtcDateAtMidnight()
    {
        IslandTime.Today.Should().Be(DateTime.UtcNow.Date);
        IslandTime.Today.Kind.Should().Be(DateTimeKind.Utc);
        IslandTime.Today.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    // 00:30 in a +02:00 zone is still 22:30 UTC of the previous day. The game day (UTC) and the wall-clock
    // day (local) disagree by one here, exactly the off-by-one that produced the 0/24 report. UTC bucketing
    // attributes the cycle to the day it belongs to in-game.
    [Test]
    public void CompletionAfterLocalMidnight_ButBeforeUtcMidnight_BucketsToPreviousUtcDay()
    {
        var localCompletion = new DateTimeOffset(2026, 6, 16, 0, 30, 0, TimeSpan.FromHours(2));
        var gameDay = localCompletion.UtcDateTime.Date;
        var wallClockDay = localCompletion.Date;

        gameDay.Should().Be(new DateTime(2026, 6, 15), "the in-game day rolls at UTC midnight, not local midnight");
        wallClockDay.Should().Be(new DateTime(2026, 6, 16));
        gameDay.Should().NotBe(wallClockDay, "this off-by-one is the 0/24 desync; the counter must use the UTC game day");
    }

    // The auto-prefill stamp and the "Done today" counter share IslandTime.Today, so an auto-recorded
    // island always satisfies the counter's predicate on the day it was recorded.
    [Test]
    public void RecordStampedWithIslandToday_SatisfiesTodayCounterPredicate()
    {
        var record = new OwnerCycleRecord
        {
            Date = IslandTime.Today,
            RecordType = CycleRecordType.Islands,
            IslandCount = 24
        };

        (record.Date.Date == IslandTime.Today).Should().BeTrue();
    }
}

// Regression for the PlotTypeExtensions fixes: the pasture extended-cycle (52h) detection must match the
// PARSED AnimalType token exactly, not a Contains on the whole config blob.
[TestFixture]
public class PastureCycleHoursTests
{
    private static string PastureConfig(string animal) => $"AnimalType: {animal}";

    [TestCase("Ox")]
    [TestCase("Horse")]
    [TestCase("Foal")]
    public void GetBaseCollectionHours_ExtendedCycleAnimal_Returns52h(string animal)
    {
        PlotType.Pasture.GetBaseCollectionHours(PastureConfig(animal)).Should().Be(52.0);
    }

    [TestCase("Cow")]
    [TestCase("Goat")]
    [TestCase("Chicken")]
    public void GetBaseCollectionHours_BaseCycleAnimal_Returns22h(string animal)
    {
        PlotType.Pasture.GetBaseCollectionHours(PastureConfig(animal)).Should().Be(22.0);
    }

    [Test]
    public void GetBaseCollectionHours_AnimalNameMerelyContainingOx_DoesNotTrip52hCycle()
    {
        // "Oxtongue" contains "Ox" but is not the Ox animal — the old Contains check wrongly returned 52h.
        PlotType.Pasture.GetBaseCollectionHours(PastureConfig("Oxtongue")).Should().Be(22.0);
    }
}

// Regression for F1: FormatSentElapsed must be fed the dispatch START time, not the future return time,
// or it always reports "just now". The production call sites pass ReadyAtUtc - cycle as the start.
[TestFixture]
public class FormatSentElapsedTests
{
    [Test]
    public void FormatSentElapsed_DispatchStartTwoHoursAgo_RendersSentAgo()
    {
        var start = DateTime.UtcNow.AddHours(-2);
        var text = LaborerSnapshot.FormatSentElapsed(DateTime.UtcNow, start);

        text.Should().StartWith("Sent ");
        text.Should().Contain("2h");
        text.Should().NotBe("just now");
    }

    [Test]
    public void FormatSentElapsed_FutureReturnTimeFedDirectly_IsTheBug_ProducesJustNow()
    {
        // Demonstrates why the start time (not the future return) must be passed: a future timestamp
        // yields a negative elapsed → "just now". The production code now subtracts the cycle first.
        var futureReturn = DateTime.UtcNow.AddHours(20);
        LaborerSnapshot.FormatSentElapsed(DateTime.UtcNow, futureReturn).Should().Be("just now");
    }
}

// Regression for G5: the dead "Sent" laborer tier was retired — a dispatched house laborer renders the
// same ("on_job") whether resolved live or from persisted config, so the on/off-island states agree.
[TestFixture]
public class SentTierRetiredTests
{
    [Test]
    public void OfflineDispatchedLaborer_AggregateIsOnJob_NeverSent()
    {
        // Two configured laborers: slot 1 dispatched (future return), slot 2 not — so AllLaborersSent is
        // false and the per-slot indicator is observable rather than blanked.
        var plot = new IslandPlot(PlotType.House, 1);
        var dict = new Dictionary<string, string>
        {
            [LaborerConfigHelper.LaborerKey(1)] = "Metalworker",
            [LaborerConfigHelper.DispatchTimeKey(1)] = LaborerConfigHelper.FormatUtc(DateTime.UtcNow.AddHours(2)),
            [LaborerConfigHelper.LaborerKey(2)] = "Woodworker"
        };
        plot.Configuration = LaborerConfigHelper.BuildConfiguration(dict);

        plot.UpdateLaborerStatuses([]); // offline path (no live snapshots)

        // The retired Sent tier must never appear — a dispatched laborer is on_job, never "sent".
        plot.Laborer1IndicatorState.Should().Be("on_job", "the retired Sent tier must resolve to on_job");
        plot.Laborer1IndicatorState.Should().NotBe("sent");
    }

    [Test]
    public void LaborerLiveStatus_HasNoSentMember()
    {
        Enum.GetNames(typeof(LaborerLiveStatus)).Should().NotContain("Sent");
    }
}

// Regression for G8b: WorldToNearestSlot must reject an off-grid position rather than snap it to a far slot.
[TestFixture]
public class SlotDistanceCutoffTests
{
    [Test]
    public void WorldToNearestSlot_FarOffGridPosition_ReturnsNull()
    {
        var layout = IslandLayouts.Get(IslandLayouts.PlayerStandard);

        // A wildly out-of-range world position projects far from every slot center (> 50px) → no match.
        layout.WorldToNearestSlot(100000f, 100000f).Should().BeNull();
    }

    [Test]
    public void WorldToNearestSlot_OnSlotPosition_StillMatches()
    {
        var layout = IslandLayouts.Get(IslandLayouts.PlayerStandard);

        // The calibrated position over slot 5 is within the cutoff and still resolves.
        layout.WorldToNearestSlot(143.8f, 136f).Should().Be(5);
    }
}

[TestFixture]
public class LaborerSnapshotParseTests
{
    [Test]
    public void TrySetTypeFromJournal_ValidMetalworkerJournal_SetsTypeAndTier()
    {
        var snapshot = new LaborerSnapshot(1L);

        // Journal names include a suffix (FULL/EMPTY): T4_JOURNAL_METALWORKER_FULL → type = "METALWORKER_FULL"
        snapshot.TrySetTypeFromJournal("T4_JOURNAL_METALWORKER_FULL");

        snapshot.LaborerType.Should().Be("METALWORKER_FULL");
        snapshot.BuildingTier.Should().Be(4);
    }

    [Test]
    public void TrySetTypeFromJournal_WoodJournal_SetsTypeAndTier()
    {
        var snapshot = new LaborerSnapshot(2L);

        snapshot.TrySetTypeFromJournal("T7_JOURNAL_WOOD_FULL");

        snapshot.LaborerType.Should().Be("WOOD_FULL");
        snapshot.BuildingTier.Should().Be(7);
    }

    [Test]
    public void TrySetTypeFromJournal_EmptyString_DoesNotThrow()
    {
        var snapshot = new LaborerSnapshot(3L);
        var act = () => snapshot.TrySetTypeFromJournal(string.Empty);
        act.Should().NotThrow();
    }

    [Test]
    public void TrySetTypeFromJournal_MalformedString_DoesNotThrow()
    {
        var snapshot = new LaborerSnapshot(4L);
        var act = () => snapshot.TrySetTypeFromJournal("NOTAJOURNAL");
        act.Should().NotThrow();
    }

    [Test]
    public void LaborerSnapshot_NewInstance_DefaultStateIsHome()
    {
        var snapshot = new LaborerSnapshot(99L);

        snapshot.IsOnJob.Should().BeFalse();
        snapshot.IsLootReady.Should().BeFalse();
        snapshot.ObjectId.Should().Be(99L);
    }
}

// Replays the real captured packet layouts (objId 383, journal 11906 = T5_JOURNAL_MAGE_FULL) through
// the live snapshot state machine. Times are relative to now so the assertions stay deterministic;
// the param SHAPES match the capture (incl. the static Feb food dates in param 6/7, which must be
// ignored). Proves the runtime behaviour the unit-level event tests do not: latching, no flip-flop,
// and time-derived loot-ready.
[TestFixture]
public class LaborerLiveStateReplayTests
{
    private const int MageJournalFull = 11906;
    private const long FebFoodTicks6 = 639075425971988263L; // 2026-02-24 — static, must be ignored
    private const long FebFoodTicks7 = 639073553971988263L; // 2026-02-22 — static, must be ignored

    private static StatisticsAnalysisTool.Network.Events.LaborerObjectInfoEvent Info56(long id, DateTime? returnAt)
    {
        var p = new Dictionary<byte, object>
        {
            { 0, id }, { 1, "David" }, { 2, "Hay" },
            { 3, 360760000L }, { 4, 6250000L }, { 5, 6250000L },
            { 6, FebFoodTicks6 }, { 7, FebFoodTicks7 }, { 10, "" }
        };
        if (returnAt.HasValue)
        {
            p[8] = returnAt.Value.Ticks;   // param 8 = return time (only present while on job)
            p[9] = new byte[16];
        }
        return new StatisticsAnalysisTool.Network.Events.LaborerObjectInfoEvent(p);
    }

    private static StatisticsAnalysisTool.Network.Events.LaborerObjectJobInfoEvent Job57Active(long id, DateTime jobStart)
        => new(new Dictionary<byte, object> { { 0, id }, { 1, true }, { 2, MageJournalFull }, { 3, 7200000L }, { 5, jobStart.Ticks } });

    private static StatisticsAnalysisTool.Network.Events.LaborerObjectJobInfoEvent Job57Bare(long id)
        => new(new Dictionary<byte, object> { { 0, id } });

    [Test]
    public void AwayLaborer_ReturnInFuture_IsOnJob_NotLootReady()
    {
        var snap = new LaborerSnapshot(383L);
        snap.UpdateFromLaborerObjectInfo(Info56(383L, DateTime.UtcNow.AddHours(20)));
        snap.UpdateFromJobInfo(Job57Active(383L, DateTime.UtcNow.AddHours(-2)));

        snap.IsOnJob.Should().BeTrue();
        snap.IsLootReady.Should().BeFalse("return time is in the future — this is the bug that showed away laborers as loot-ready");
        snap.JournalItemId.Should().Be(MageJournalFull);
    }

    [Test]
    public void AwayLaborer_BareAndNoParam8Packets_DoNotClearOnJob()
    {
        var snap = new LaborerSnapshot(383L);
        snap.UpdateFromLaborerObjectInfo(Info56(383L, DateTime.UtcNow.AddHours(20)));
        snap.UpdateFromJobInfo(Job57Active(383L, DateTime.UtcNow.AddHours(-2)));

        // Both forms are broadcast WHILE the laborer is still out (every capture interleaves them).
        snap.UpdateFromJobInfo(Job57Bare(383L));
        snap.UpdateFromLaborerObjectInfo(Info56(383L, null));

        snap.IsOnJob.Should().BeTrue("on-job must latch through bare/no-param8 packets (no flip-flop)");
        snap.JobDispatchTime.Should().NotBeNull("return time must be retained when a packet omits param 8");
        snap.IsLootReady.Should().BeFalse();
    }

    [Test]
    public void Laborer_ReturnPassed_BecomesLootReadyByTime()
    {
        var snap = new LaborerSnapshot(383L);
        snap.UpdateFromLaborerObjectInfo(Info56(383L, DateTime.UtcNow.AddHours(-1)));
        snap.UpdateFromJobInfo(Job57Active(383L, DateTime.UtcNow.AddHours(-23)));

        snap.HasActiveJob.Should().BeTrue();
        snap.IsLootReady.Should().BeTrue("return time has passed");
        snap.IsOnJob.Should().BeFalse("a returned laborer is loot-ready, not on-job — this keeps 'all on job' from firing the webhook mid-collection");
    }

    [Test]
    public void NotOnJob_FebFoodDates_NeverProduceReadyAt()
    {
        var snap = new LaborerSnapshot(383L);
        snap.UpdateFromLaborerObjectInfo(Info56(383L, null)); // no param 8 = not dispatched

        snap.IsOnJob.Should().BeFalse();
        snap.ReadyAtUtc.Should().BeNull("param 6/7 are food timestamps, not job times");
        snap.IsLootReady.Should().BeFalse();
    }

    [Test]
    public void ReconnectNoParam8_ReadyAtDerivedFromJobStartPlusCycle()
    {
        var snap = new LaborerSnapshot(383L);
        // Reconnect path: only job-info (no LaborerObjectInfo param 8). jobStart 23h ago + 22h cycle = ready.
        snap.UpdateFromJobInfo(Job57Active(383L, DateTime.UtcNow.AddHours(-23)));

        snap.HasActiveJob.Should().BeTrue();
        snap.IsLootReady.Should().BeTrue("jobStart + 22h base cycle is already in the past");
        snap.IsOnJob.Should().BeFalse("return time has passed → loot-ready, not on-job");
    }

    [Test]
    public void LootReadyLaborer_IsNotOnJob_KeepsWebhookGateClosed()
    {
        // Regression guard: the owner "all on job" webhook gate is snapshots.All(s => s.IsOnJob).
        // A laborer with loot ready (return passed, not yet re-dispatched) must report IsOnJob = false,
        // or the popup fires mid-collection (which it did when IsOnJob was a sticky latched bool).
        var ready = new LaborerSnapshot(1L);
        ready.UpdateFromLaborerObjectInfo(Info56(1L, DateTime.UtcNow.AddHours(-1)));
        ready.UpdateFromJobInfo(Job57Active(1L, DateTime.UtcNow.AddHours(-23)));

        ready.IsLootReady.Should().BeTrue();
        ready.IsOnJob.Should().BeFalse("a loot-ready laborer must not satisfy the all-on-job webhook gate");
    }
}

[TestFixture]
public class IslandSlotLabelTests
{
    [Test]
    public void SetSlotLabel_ValidLabel_GetSlotLabelReturnsIt()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);

        island.SetSlotLabel(2, "My Farm");

        island.GetSlotLabel(2).Should().Be("My Farm");
    }

    [Test]
    public void SetSlotLabel_EmptyLabel_RemovesLabel()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);
        island.SetSlotLabel(2, "My Farm");

        island.SetSlotLabel(2, "");

        island.GetSlotLabel(2).Should().BeEmpty();
    }

    [Test]
    public void SetSlotLabel_WhitespaceLabel_RemovesLabel()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);
        island.SetSlotLabel(1, "Something");

        island.SetSlotLabel(1, "   ");

        island.GetSlotLabel(1).Should().BeEmpty();
    }

    [Test]
    public void GetSlotLabel_UnsetSlot_ReturnsEmpty()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);

        island.GetSlotLabel(99).Should().BeEmpty();
    }

    [Test]
    public void SetSlotLabel_TrimsWhitespace()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);

        island.SetSlotLabel(0, "  HerbGarden  ");

        island.GetSlotLabel(0).Should().Be("HerbGarden");
    }
}

[TestFixture]
public class IslandCollectionStatusTests
{
    [Test]
    public void CollectionStatus_NotPlanted_ReturnsNotPlantedText()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);

        island.CollectionStatusText.Should().Be("ISLAND_MANAGEMENT_STATUS_NOT_PLANTED");
        island.IsCollectionReady.Should().BeFalse();
        island.NeedsVisit.Should().BeTrue();
    }

    [Test]
    public void CollectionStatus_PlantedRecently_NotReady()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);
        island.Plots.Add(new IslandPlot(PlotType.House, 1));
        island.LastCycleStartAt = DateTime.UtcNow;

        island.IsCollectionReady.Should().BeFalse();
        island.NeedsVisit.Should().BeFalse();
    }

    [Test]
    public void CollectionStatus_PlantedLongAgo_IsCollectionReady()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);
        island.Plots.Add(new IslandPlot(PlotType.House, 1));
        island.LastCycleStartAt = DateTime.UtcNow.AddHours(-30);

        island.IsCollectionReady.Should().BeTrue();
        island.NeedsVisit.Should().BeTrue();
        island.CollectionStatusText.Should().Be("ISLAND_MANAGEMENT_STATUS_COLLECTION_READY");
    }

    [Test]
    public void CollectionStatusState_NotPlanted_ReturnsDefault()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);

        island.CollectionStatusState.Should().Be("default");
    }

    [Test]
    public void CollectionStatusState_Planted_ReturnsPlanted()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);
        island.Plots.Add(new IslandPlot(PlotType.House, 1));
        island.LastCycleStartAt = DateTime.UtcNow;
        island.LastHandledAt = DateTime.UtcNow;

        island.CollectionStatusState.Should().Be("planted");
    }

    [Test]
    public void CollectionStatusState_Ready_ReturnsReady()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);
        island.Plots.Add(new IslandPlot(PlotType.House, 1));
        island.LastCycleStartAt = DateTime.UtcNow.AddHours(-30);

        island.CollectionStatusState.Should().Be("ready");
    }
}

[TestFixture]
public class IslandTrackingCountTests
{
    [Test]
    public void TotalLaborersSent_DefaultsToZero()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);

        island.TotalLaborersSent.Should().Be(0);
    }

    [Test]
    public void TotalLaborersSent_Increment_UpdatesCorrectly()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);

        island.TotalLaborersSent++;
        island.TotalLaborersSent++;

        island.TotalLaborersSent.Should().Be(2);
    }

    [Test]
    public void TotalLootCollected_Increment_UpdatesCorrectly()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);

        island.TotalLootCollected++;

        island.TotalLootCollected.Should().Be(1);
    }

    [Test]
    public void LastVisited_SetAndGet_Roundtrips()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);
        var now = DateTime.UtcNow;

        island.LastVisited = now;

        island.LastVisited.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));
    }
}

[TestFixture]
public class IslandPlotPersistedStatusTests
{
    [Test]
    public void MatchStatus_EmptySnapshots_LootReadyFlagTrue_ReturnsLootReady()
    {
        var plot = new IslandPlot(PlotType.House, 1);
        var dict = new Dictionary<string, string>
        {
            [LaborerConfigHelper.LaborerKey(1)] = "Metalworker",
            [LaborerConfigHelper.LootReadyKey(1)] = "true"
        };
        plot.Configuration = LaborerConfigHelper.BuildConfiguration(dict);

        plot.UpdateLaborerStatuses([]);

        plot.Laborer1IndicatorState.Should().Be("loot_ready");
    }

    [Test]
    public void MatchStatus_EmptySnapshots_DispatchTimeFuture_ReturnsOnJobWithTimeRemaining()
    {
        var plot = new IslandPlot(PlotType.House, 1);
        var readyAt = DateTime.UtcNow.AddHours(2);
        var dict = new Dictionary<string, string>
        {
            [LaborerConfigHelper.LaborerKey(1)] = "Metalworker",
            [LaborerConfigHelper.DispatchTimeKey(1)] = LaborerConfigHelper.FormatUtc(readyAt)
        };
        plot.Configuration = LaborerConfigHelper.BuildConfiguration(dict);

        plot.UpdateLaborerStatuses([]);

        plot.PlotSentState.Should().Be("on_job");
        plot.Laborer1TimeRemaining.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void MatchStatus_EmptySnapshots_DispatchTimePast_ReturnsLootReady()
    {
        var plot = new IslandPlot(PlotType.House, 1);
        var dispatchAt = DateTime.UtcNow.AddHours(-23); // 22h job + 1h past ready
        var dict = new Dictionary<string, string>
        {
            [LaborerConfigHelper.LaborerKey(1)] = "Metalworker",
            [LaborerConfigHelper.DispatchTimeKey(1)] = LaborerConfigHelper.FormatUtc(dispatchAt)
        };
        plot.Configuration = LaborerConfigHelper.BuildConfiguration(dict);

        plot.UpdateLaborerStatuses([]);

        plot.Laborer1IndicatorState.Should().Be("loot_ready");
    }

    [Test]
    public void MatchStatus_EmptySnapshots_NoKeys_ReturnsNone()
    {
        var plot = new IslandPlot(PlotType.House, 1);

        plot.UpdateLaborerStatuses([]);

        plot.Laborer1IndicatorState.Should().Be("none");
    }

    [Test]
    public void PlotCollectionCountdown_PlantedAt10hAgo_22hCycle_ReturnsApprox12hRemaining()
    {
        var plot = new IslandPlot(PlotType.Farm, 1);
        plot.PlotPlantedAt = DateTime.UtcNow.AddHours(-10);

        var countdown = plot.PlotCollectionCountdown;

        // Remaining ~12h; formatted as "Nh Mm" — just verify it's non-empty and contains "h "
        countdown.Should().NotBeEmpty();
        countdown.Should().Contain("h ");
    }

    [Test]
    public void PlotCollectionCountdown_PlantedAt23hAgo_22hCycle_ReturnsReady()
    {
        var plot = new IslandPlot(PlotType.Farm, 1);
        plot.PlotPlantedAt = DateTime.UtcNow.AddHours(-23);

        plot.PlotCollectionCountdown.Should().Be("ISLAND_MANAGEMENT_STATUS_READY");
    }

    [Test]
    public void PlotCollectionCountdown_NoPlantedAt_ReturnsEmpty()
    {
        var plot = new IslandPlot(PlotType.Farm, 1);

        plot.PlotCollectionCountdown.Should().BeEmpty();
    }

    [Test]
    public void LaborerConfigHelper_FormatAndParse_Roundtrip()
    {
        var original = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var formatted = LaborerConfigHelper.FormatUtc(original);

        var dict = new Dictionary<string, string> { ["key"] = formatted };
        var built = LaborerConfigHelper.BuildConfiguration(dict);
        var parsed = LaborerConfigHelper.ParseConfiguration(built);

        LaborerConfigHelper.TryParseUtc(parsed["key"], out var result).Should().BeTrue();
        result.Should().BeCloseTo(original, TimeSpan.FromSeconds(1));
    }
}

[TestFixture]
public class FarmablePlotTypeClassificationTests
{
    // Herb seeds carry the "_FARM_" token but are herb-garden plants — they must resolve by the herb
    // name, not be bucketed as Farm. Guards the unified classifier (FarmablePlotData) used for plot
    // typing, slot assignment and yield bucketing.
    [TestCase("T6_FARM_FOXGLOVE_SEED", PlotType.HerbGarden)]
    [TestCase("T8_FARM_YARROW_SEED", PlotType.HerbGarden)]
    [TestCase("T5_FARM_TEASEL_SEED", PlotType.HerbGarden)]
    [TestCase("T1_FARM_CARROT_SEED", PlotType.Farm)]
    [TestCase("T7_FARM_CORN_SEED", PlotType.Farm)]
    [TestCase("T5_FARM_CABBAGE_SEED", PlotType.Farm)]
    [TestCase("T8_FARM_COW_BABY", PlotType.Pasture)]
    [TestCase("T3_FARM_CHICKEN_BABY", PlotType.Pasture)]
    public void TryResolveFarmablePlotInfo_ByUniqueName_ResolvesExpectedPlotType(string uniqueName, PlotType expected)
    {
        var info = PlotTypeExtensions.TryResolveFarmablePlotInfo(uniqueName);

        info.Should().NotBeNull();
        info.PlotType.Should().Be(expected);
    }
}

[TestFixture]
public class IslandPlotSlotDotsTests
{
    [Test]
    public void SlotDots_WithPerTilePlantedAts_RendersIndividualSlotStates()
    {
        var plot = new IslandPlot(PlotType.HerbGarden, 1) { Configuration = "CropType: Foxglove Seeds" };
        var now = DateTime.UtcNow;

        // tile 0 just planted (growing), tile 1 long past cycle (ready), tile 2 empty (collected).
        plot.SetTilePlantedAts(new DateTime?[] { now, now.AddHours(-1000), null });

        var dots = plot.SlotDots;

        dots.Should().HaveCount(9); // HerbGarden = 9 slots, padded with "home"
        dots[0].Should().Be("on_job");
        dots[1].Should().Be("loot_ready");
        dots[2].Should().Be("home");
        dots[8].Should().Be("home");
    }

    [Test]
    public void SlotDots_KennelHasFourSlots()
    {
        var plot = new IslandPlot(PlotType.Kennel, 1);
        plot.SetTilePlantedAts(new DateTime?[] { DateTime.UtcNow });

        plot.SlotDots.Should().HaveCount(4);
    }

    [Test]
    public void SlotDots_NoTileData_FallsBackToAggregate()
    {
        var plot = new IslandPlot(PlotType.Farm, 1);
        // No per-tile data and no PlotPlantedAt → all slots "home".
        plot.SlotDots.Should().OnlyContain(s => s == "home");
    }
}

[TestFixture]
public class IslandLayoutTransformTests
{
    // The per-plot timer resolver maps a farmable plant's world position to its plot card via this transform.
    // Lock the calibrated player-standard WorldTransform so a regression in the affine coefficients (which
    // would silently break per-plot timer set/clear) is caught here.
    [Test]
    public void WorldToNearestSlot_PlayerStandard_HasCalibratedTransform()
    {
        var layout = IslandLayouts.Get(IslandLayouts.PlayerStandard);
        layout.Should().NotBeNull();

        // A world position over the slot-5 region resolves to slot 5 (pixel ≈ 323,436 vs slot 5 at 324,439).
        layout.WorldToNearestSlot(143.8f, 136f).Should().Be(5);
    }

    [Test]
    public void WorldToNearestSlot_RequireLarge_NeverReturnsSmallSlot()
    {
        var layout = IslandLayouts.Get(IslandLayouts.PlayerStandard);

        // Small slots are 17/18; requiring large must exclude them regardless of nearest pixel.
        var slot = layout.WorldToNearestSlot(143.8f, 136f, requireLarge: true);
        slot.Should().NotBeNull();
        slot.Should().NotBe(17);
        slot.Should().NotBe(18);
    }
}
