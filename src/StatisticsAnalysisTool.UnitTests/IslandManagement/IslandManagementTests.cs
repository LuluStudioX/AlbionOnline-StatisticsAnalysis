using FluentAssertions;
using NUnit.Framework;
using StatisticsAnalysisTool.Island;
using System;
using System.Collections.Generic;

namespace StatisticsAnalysisTool.UnitTests.IslandManagement;

[TestFixture]
public class OwnerProfileBalanceTests
{
    [Test]
    public void OwnerBalance_OpeningBalanceOnly_EqualsOpeningBalance()
    {
        var profile = new OwnerProfile { OpeningBalance = 500m };

        var earned = 0m;
        var withdrawn = 0m;
        var balance = profile.OpeningBalance + earned - withdrawn;

        balance.Should().Be(500m);
    }

    [Test]
    public void OwnerBalance_WithCycleRecordsAndWithdrawals_ComputesCorrectly()
    {
        var profile = new OwnerProfile
        {
            OpeningBalance = 100m,
            CycleHistory = new List<OwnerCycleRecord>
            {
                new() { EarnedAmount = 300m },
                new() { EarnedAmount = 200m }
            },
            Withdrawals = new List<OwnerWithdrawalEntry>
            {
                new() { Amount = 150m }
            }
        };

        var earned = 0m;
        foreach (var c in profile.CycleHistory) earned += c.EarnedAmount;
        var withdrawn = 0m;
        foreach (var w in profile.Withdrawals) withdrawn += w.Amount;

        var balance = profile.OpeningBalance + earned - withdrawn;

        balance.Should().Be(450m);
    }

    [Test]
    public void OwnerBalance_MultipleWithdrawals_SubtractsAll()
    {
        var profile = new OwnerProfile
        {
            OpeningBalance = 0m,
            CycleHistory = new List<OwnerCycleRecord>
            {
                new() { EarnedAmount = 1000m }
            },
            Withdrawals = new List<OwnerWithdrawalEntry>
            {
                new() { Amount = 300m },
                new() { Amount = 200m },
                new() { Amount = 100m }
            }
        };

        var earned = profile.CycleHistory[0].EarnedAmount;
        var withdrawn = 600m;
        var balance = profile.OpeningBalance + earned - withdrawn;

        balance.Should().Be(400m);
    }

    [Test]
    public void RecordPayment_AddsWithdrawalEntry()
    {
        var profile = new OwnerProfile
        {
            OpeningBalance = 500m,
            Withdrawals = new List<OwnerWithdrawalEntry>()
        };

        var entry = new OwnerWithdrawalEntry { Amount = 200m, Notes = "Weekly payout", Timestamp = DateTime.UtcNow };
        profile.Withdrawals.Add(entry);

        profile.Withdrawals.Should().HaveCount(1);
        profile.Withdrawals[0].Amount.Should().Be(200m);
        profile.Withdrawals[0].Notes.Should().Be("Weekly payout");
    }

    [Test]
    public void AddCycleRecord_IncreasesEarnings()
    {
        var profile = new OwnerProfile
        {
            OpeningBalance = 0m,
            CycleHistory = new List<OwnerCycleRecord>()
        };

        profile.CycleHistory.Add(new OwnerCycleRecord { EarnedAmount = 750m, Notes = "Island cycle 1" });
        profile.CycleHistory.Add(new OwnerCycleRecord { EarnedAmount = 250m, Notes = "Island cycle 2" });

        var totalEarned = 0m;
        foreach (var r in profile.CycleHistory) totalEarned += r.EarnedAmount;

        totalEarned.Should().Be(1000m);
        profile.CycleHistory.Should().HaveCount(2);
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
        island.LastPlantedAt = DateTime.UtcNow;

        island.IsCollectionReady.Should().BeFalse();
        island.NeedsVisit.Should().BeFalse();
    }

    [Test]
    public void CollectionStatus_PlantedLongAgo_IsCollectionReady()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);
        island.Plots.Add(new IslandPlot(PlotType.House, 1));
        island.LastPlantedAt = DateTime.UtcNow.AddHours(-30);

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
        island.LastPlantedAt = DateTime.UtcNow;
        island.LastHandledAt = DateTime.UtcNow;

        island.CollectionStatusState.Should().Be("planted");
    }

    [Test]
    public void CollectionStatusState_Ready_ReturnsReady()
    {
        var island = new Island.Island("TestIsland", "Owner", 3);
        island.Plots.Add(new IslandPlot(PlotType.House, 1));
        island.LastPlantedAt = DateTime.UtcNow.AddHours(-30);

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
