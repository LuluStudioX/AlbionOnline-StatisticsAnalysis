using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Diagnostics;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace StatisticsAnalysisTool.Network.Events;

// EVENT [45]NewBuilding - map[0:<objectId> 1:<buildingGuid:Byte[]> 2:<houseObjectId> 3:<uniqueName>
//   4:<position:Single[]> 7:<nutrition> 8:<lastActionTicks> 9:<housePlotGuid:Byte[]>
//   10:<Byte[]> 11:<islandOwnerName> 12:<islandOwnerName> 13:<laborerFirstName> 14:<laborerLastName>
//   16:<hasPremium:bool> 18:<slots> 252:45]
public class NewBuildingEvent
{
    public long ObjectId { get; private set; } = -1;
    public Guid BuildingGuid { get; private set; }
    public string UniqueName { get; private set; } = string.Empty;
    public int Nutrition { get; private set; }
    public bool HasPremium { get; private set; }
    public string IslandOwnerName { get; private set; } = string.Empty;
    public (float X, float Y)? Position { get; private set; }
    public Guid HousePlotGuid { get; private set; }
    public string LaborerFirstName { get; private set; } = string.Empty;
    public string LaborerLastName { get; private set; } = string.Empty;
    public DateTime? PlantedAt { get; private set; }

    public NewBuildingEvent(Dictionary<byte, object> parameters)
    {
        try
        {
            if (parameters.TryGetValue(0, out var p0))
                ObjectId = p0.ObjectToLong() ?? -1;

            if (parameters.TryGetValue(1, out var p1) && p1 is byte[] p1Bytes && p1Bytes.Length == 16)
                BuildingGuid = new Guid(p1Bytes);

            if (parameters.TryGetValue(4, out var p4))
            {
                float px = 0, py = 0;
                bool parsed = false;
                if (p4 is float[] fa && fa.Length >= 2)
                {
                    px = fa[0];
                    py = fa[1];
                    parsed = true;
                }
                else if (p4 is int[] ia && ia.Length >= 2)
                {
                    px = ia[0];
                    py = ia[1];
                    parsed = true;
                }
                else if (p4 is object[] oa && oa.Length >= 2
                    && float.TryParse(oa[0]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out px)
                    && float.TryParse(oa[1]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out py))
                {
                    parsed = true;
                }
                if (parsed)
                    Position = (px, py);
            }

            if (parameters.TryGetValue(3, out var p3))
                UniqueName = p3?.ToString() ?? string.Empty;

            if (parameters.TryGetValue(9, out var p9) && p9 is byte[] p9Bytes && p9Bytes.Length == 16)
                HousePlotGuid = new Guid(p9Bytes);

            if (parameters.TryGetValue(7, out var p7))
                Nutrition = p7.ObjectToInt();

            if (parameters.TryGetValue(8, out var p8))
            {
                var ticks = p8.ObjectToLong();
                if (ticks.HasValue && ticks.Value > 0)
                    PlantedAt = new DateTime(ticks.Value, DateTimeKind.Utc);
            }

            if (parameters.TryGetValue(11, out var p11))
                IslandOwnerName = p11?.ToString() ?? string.Empty;

            if (parameters.TryGetValue(13, out var p13))
                LaborerFirstName = p13?.ToString() ?? string.Empty;

            if (parameters.TryGetValue(14, out var p14))
                LaborerLastName = p14?.ToString() ?? string.Empty;

            if (parameters.TryGetValue(16, out var p16))
                HasPremium = p16.ObjectToBool();
        }
        catch (Exception e)
        {
            DebugConsole.WriteError(MethodBase.GetCurrentMethod()?.DeclaringType, e);
        }
    }

    public bool IsLaborerBuilding => UniqueName.Contains("LABOURER", StringComparison.OrdinalIgnoreCase);
}
