using FluentAssertions;
using NUnit.Framework;
using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Network.Events;

namespace StatisticsAnalysisTool.UnitTests.Network.Events;

[TestFixture]
public class NewBuildingEventTests
{
    // EVENT [45] NewBuilding — laborer house packet captured from packets-20260416.log
    // 0:710 1:System.Byte[] 2:555 3:T7_LABOURER_WOOD 4:System.Single[] 7:500000
    //   8:639118872837704493 9:System.Byte[] 10:System.Byte[] 11:OrangeZones 12:OrangeZones
    //   13:Alwin 14:Myrus 18:10000 19:-1 20:639118872837704493 22:... 29:0 252:45

    private static readonly byte[] BuildingGuidBytes =
    [
        0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0,
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88
    ];

    private static readonly byte[] HousePlotGuidBytes =
    [
        0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff, 0x00, 0x11,
        0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99
    ];

    [Test]
    public void Constructor_LaborerHousePacket_ParsesAllFields()
    {
        var parameters = new Dictionary<byte, object>
        {
            { 0, 710L },
            { 1, BuildingGuidBytes },
            { 3, "T7_LABOURER_WOOD" },
            { 7, 500000 },
            { 9, HousePlotGuidBytes },
            { 13, "Alwin" },
            { 14, "Myrus" },
            { 16, true }
        };

        var evt = new NewBuildingEvent(parameters);

        evt.ObjectId.Should().Be(710);
        evt.BuildingGuid.Should().Be(new Guid(BuildingGuidBytes));
        evt.UniqueName.Should().Be("T7_LABOURER_WOOD");
        evt.Nutrition.Should().Be(500000);
        evt.HousePlotGuid.Should().Be(new Guid(HousePlotGuidBytes));
        evt.LaborerFirstName.Should().Be("Alwin");
        evt.LaborerLastName.Should().Be("Myrus");
        evt.HasPremium.Should().BeTrue();
        evt.IsLaborerBuilding.Should().BeTrue();
    }

    [Test]
    public void Constructor_NonLaborerBuilding_IsLaborerBuildingFalse()
    {
        var parameters = new Dictionary<byte, object>
        {
            { 0, 287L },
            { 3, "STEPPE_GREEN_T1_BANK_ISLAND" }
        };

        var evt = new NewBuildingEvent(parameters);

        evt.IsLaborerBuilding.Should().BeFalse();
    }

    [Test]
    public void Constructor_MinimalPacket_OnlyObjectId()
    {
        // Real packets sometimes only have objectId + Byte[] params
        var parameters = new Dictionary<byte, object>
        {
            { 0, 1477 }
        };

        var evt = new NewBuildingEvent(parameters);

        evt.ObjectId.Should().Be(1477);
        evt.BuildingGuid.Should().Be(Guid.Empty);
        evt.HousePlotGuid.Should().Be(Guid.Empty);
        evt.IsLaborerBuilding.Should().BeFalse();
    }
}

[TestFixture]
public class LaborerObjectInfoEventTests
{
    // EVENT [56] LaborerObjectInfo — captured from packets-20260416.log
    // Home:   0:712 1:Michael 2:Foreman 3:206200000 4:5500000 5:5500000
    //         6:639086232025186603 7:638873594425186603 10: 252:56
    // On job: same + 8:639119670378338039 9:System.Byte[] 10:OrangeZones

    private static readonly byte[] JobGuidBytes =
    [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10
    ];

    [Test]
    public void Constructor_LaborerAtHome_NamesPopulatedIsOnJobFalse()
    {
        var parameters = new Dictionary<byte, object>
        {
            { 0, 712L },
            { 1, "Michael" },
            { 2, "Foreman" },
            { 3, 206200000L },
            { 4, 5500000L },
            { 5, 5500000L },
            { 6, 639086232025186603L },
            { 7, 638873594425186603L },
            { 10, "" }
        };

        var evt = new LaborerObjectInfoEvent(parameters);

        evt.ObjectId.Should().Be(712);
        evt.FirstName.Should().Be("Michael");
        evt.LastName.Should().Be("Foreman");
        evt.IsOnJob.Should().BeFalse();
        evt.JobDispatchTime.Should().BeNull();
        evt.ActiveJobId.Should().BeNull();
    }

    [Test]
    public void Constructor_LaborerOnJob_IsOnJobTrueWithDispatchTime()
    {
        var dispatchTicks = 639119670378338039L;

        var parameters = new Dictionary<byte, object>
        {
            { 0, 712L },
            { 1, "Michael" },
            { 2, "Foreman" },
            { 3, 206200000L },
            { 4, 5500000L },
            { 5, 5500000L },
            { 6, 639086232025186603L },
            { 7, 638873594425186603L },
            { 8, dispatchTicks },
            { 9, JobGuidBytes },
            { 10, "OrangeZones" }
        };

        var evt = new LaborerObjectInfoEvent(parameters);

        evt.ObjectId.Should().Be(712);
        evt.FirstName.Should().Be("Michael");
        evt.LastName.Should().Be("Foreman");
        evt.IsOnJob.Should().BeTrue();
        evt.JobDispatchTime.Should().Be(new DateTime(dispatchTicks, DateTimeKind.Utc));
        evt.ActiveJobId.Should().Be(new Guid(JobGuidBytes));
        evt.SentByCharacter.Should().Be("OrangeZones");
    }

    [Test]
    public void Constructor_LaborerHome_Param8Absent_IsOnJobFalse()
    {
        var parameters = new Dictionary<byte, object>
        {
            { 0, 710L },
            { 1, "Alwin" },
            { 2, "Myrus" }
        };

        var evt = new LaborerObjectInfoEvent(parameters);

        evt.IsOnJob.Should().BeFalse();
        evt.JobDispatchTime.Should().BeNull();
    }
}

[TestFixture]
public class LaborerObjectJobInfoEventTests
{
    // EVENT [57] LaborerObjectJobInfo — two forms seen across every island capture:
    //   Active job: 0:712 1:True 2:11799 3:3600000 5:639118868011736295 252:57  (param 1 always true)
    //   Idle/bare:  0:712 252:57
    // Param 1 marks "has an active job" (away on job), NOT loot-ready. Loot-ready is time-derived
    // downstream (LaborerSnapshot.IsLootReady), so the event only exposes IsAwayOnJob.

    [Test]
    public void Constructor_ActiveJobPacket_IsAwayOnJobTrue()
    {
        var parameters = new Dictionary<byte, object>
        {
            { 0, 712L },
            { 1, true },
            { 2, 11799 },
            { 3, 3600000L }
        };

        var evt = new LaborerObjectJobInfoEvent(parameters);

        evt.ObjectId.Should().Be(712);
        evt.IsAwayOnJob.Should().BeTrue();
        evt.JournalItemId.Should().Be(11799);
        evt.CurrentFameFill.Should().Be(FixPoint.FromInternalValue(3600000));
    }

    [Test]
    public void Constructor_JournalPresent_IsAwayOnJobTrue()
    {
        // A non-zero journal id means the laborer holds an active job, regardless of param 1.
        var parameters = new Dictionary<byte, object>
        {
            { 0, 710L },
            { 2, 11799 },
            { 3, 7200000L }
        };

        var evt = new LaborerObjectJobInfoEvent(parameters);

        evt.ObjectId.Should().Be(710);
        evt.IsAwayOnJob.Should().BeTrue();
        evt.JournalItemId.Should().Be(11799);
    }

    [Test]
    public void Constructor_BareForm_IsAwayOnJobFalse()
    {
        // Only ObjectId present — no active job (idle at home or just collected).
        var parameters = new Dictionary<byte, object>
        {
            { 0, 711L }
        };

        var evt = new LaborerObjectJobInfoEvent(parameters);

        evt.ObjectId.Should().Be(711);
        evt.IsAwayOnJob.Should().BeFalse();
        evt.JournalItemId.Should().Be(0);
    }

    [Test]
    public void Constructor_ActiveJobPacket_ParsesFameCorrectly()
    {
        // Real packet: 0:187 1:True 2:12047 3:750000 5:639118447788395661
        var parameters = new Dictionary<byte, object>
        {
            { 0, 187L },
            { 1, true },
            { 2, 12047 },
            { 3, 750000L }
        };

        var evt = new LaborerObjectJobInfoEvent(parameters);

        evt.ObjectId.Should().Be(187);
        evt.IsAwayOnJob.Should().BeTrue();
        evt.JournalItemId.Should().Be(12047);
        evt.CurrentFameFill.DoubleValue.Should().BeApproximately(750000 / 10000.0, 0.001);
    }
}

[TestFixture]
public class FarmBuildingInfoEventTests
{
    // EVENT [54] FarmBuildingInfo — confirmed param map from live capture 2026-05-22:
    //   0: long  — ObjectId
    //   4: long  — elapsed grow time in 100µs units (same encoding as FarmableObjectInfo code 201)
    //   5: long  — server DateTime ticks (UTC, server's "now")
    // PlantedAt = serverNow - elapsed.

    [Test]
    public void Constructor_NoGrowthParams_PlantedAtNull()
    {
        var parameters = new Dictionary<byte, object>
        {
            { 0, 306L },
            { 2, Array.Empty<byte>() },
            { 3, Array.Empty<byte>() }
        };

        var evt = new FarmBuildingInfoEvent(parameters);

        evt.ObjectId.Should().Be(306);
        evt.PlantedAt.Should().BeNull();
    }

    [Test]
    public void Constructor_ActiveFarm_DerivesPlantedAt()
    {
        // From live capture: param 4 = 417830000 (100µs elapsed), param 5 = server ticks
        // elapsed = 417830000 / 10 ms = 41783000 ms ≈ 11.607h → PlantedAt = serverNow - elapsed
        const long elapsed100us = 417830000L;
        var serverNow = new DateTime(2026, 5, 23, 0, 11, 57, DateTimeKind.Utc);
        var serverTicks = serverNow.Ticks;
        var elapsedMs = elapsed100us / 10.0;
        var expectedPlantedAt = serverNow.AddMilliseconds(-elapsedMs);

        var parameters = new Dictionary<byte, object>
        {
            { 0, 506L },
            { 4, elapsed100us },
            { 5, serverTicks }
        };

        var evt = new FarmBuildingInfoEvent(parameters);

        evt.ObjectId.Should().Be(506);
        evt.PlantedAt.Should().BeCloseTo(expectedPlantedAt, TimeSpan.FromSeconds(1));
        evt.PlantedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void Constructor_ObjectIdOnly_PlantedAtNull()
    {
        var parameters = new Dictionary<byte, object>
        {
            { 0, 248L }
        };

        var evt = new FarmBuildingInfoEvent(parameters);

        evt.ObjectId.Should().Be(248);
        evt.PlantedAt.Should().BeNull();
    }
}

[TestFixture]
public class FarmableObjectInfoEventTests
{
    // EVENT [201] FarmableObjectInfo — confirmed param map from live capture 2026-05-23:
    //   Farm plots:   elapsed = param 4 (100µs), server now = param 5
    //   Pasture/herb: elapsed = param 1 (100µs), server now = param 2
    // PlantedAt = serverNow - elapsed.

    [Test]
    public void Constructor_ActiveFarm_DerivesPlantedAt()
    {
        // From live capture: 0:449 4:412490000 5:639150924843657424
        // In-game showed ~9h remaining → elapsed ≈ 13h → param 4 = elapsed, not remaining
        const long elapsed100us = 412490000L;
        const long serverTicks = 639150924843657424L;
        var serverNow = new DateTime(serverTicks, DateTimeKind.Utc);
        var elapsedMs = elapsed100us / 10.0;
        var expectedPlantedAt = serverNow.AddMilliseconds(-elapsedMs);

        var parameters = new Dictionary<byte, object>
        {
            { 0, 449L },
            { 4, elapsed100us },
            { 5, serverTicks },
            { 6, Array.Empty<byte>() },
            { 7, Array.Empty<byte>() },
            { 12, 1 },
            { 13, long.MinValue }
        };

        var evt = new FarmableObjectInfoEvent(parameters);

        evt.ObjectId.Should().Be(449);
        evt.PlantedAt.Should().BeCloseTo(expectedPlantedAt, TimeSpan.FromSeconds(1));
        evt.PlantedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void Constructor_ZeroElapsed_PlantedAtNull()
    {
        var parameters = new Dictionary<byte, object>
        {
            { 0, 100L },
            { 4, 0L },
            { 5, DateTime.UtcNow.Ticks }
        };

        var evt = new FarmableObjectInfoEvent(parameters);

        evt.ObjectId.Should().Be(100);
        evt.PlantedAt.Should().BeNull();
    }

    [Test]
    public void Constructor_PastureLayout_Param1And2_DerivesPlantedAt()
    {
        // From live capture: 0:457 (Baby Chickens) 1:499410000 2:639151019453900825
        // Elapsed = 499410000 / 10ms = 49941000ms = 13h 51m 54s → remaining = 22h - 13h51m54s = 8h8m6s ✓ (in-game showed ~8h8m)
        const long elapsed100us = 499410000L;
        const long serverTicks = 639151019453900825L;
        var serverNow = new DateTime(serverTicks, DateTimeKind.Utc);
        var expectedPlantedAt = serverNow.AddMilliseconds(-elapsed100us / 10.0);

        var parameters = new Dictionary<byte, object>
        {
            { 0, 457L },
            { 1, elapsed100us },
            { 2, serverTicks }
        };

        var evt = new FarmableObjectInfoEvent(parameters);

        evt.ObjectId.Should().Be(457);
        evt.PlantedAt.Should().BeCloseTo(expectedPlantedAt, TimeSpan.FromSeconds(1));
        evt.PlantedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void Constructor_NoParams_ObjectIdNegative()
    {
        var parameters = new Dictionary<byte, object>();

        var evt = new FarmableObjectInfoEvent(parameters);

        evt.ObjectId.Should().Be(-1);
        evt.PlantedAt.Should().BeNull();
    }
}
