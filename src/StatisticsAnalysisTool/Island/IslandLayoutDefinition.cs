using System;
using System.Collections.Generic;
using System.Linq;

namespace StatisticsAnalysisTool.Island;

public sealed class IslandSlotDefinition
{
    public int SlotIndex { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public bool IsLarge { get; init; }
    public int GridCol { get; init; }
    public int GridRow { get; init; }
    /// <summary>
    /// For small slots only: the SlotIndex of the adjacent small slot that pairs with this one.
    /// When a large-type plot occupies one small slot, the paired slot is treated as consumed.
    /// </summary>
    public int? PairedSmallSlotIndex { get; init; }
}

public sealed class IslandLayoutDefinition
{
    public string LayoutId { get; init; }
    public string DisplayName { get; init; }
    public string MapImageResourcePath { get; init; }
    public IReadOnlyList<IslandSlotDefinition> Slots { get; init; }
    public int GridColumns { get; init; }
    public int GridRows { get; init; }
    public (double A, double B, double C, double D, double E, double F)? WorldTransform { get; init; }

    public IslandSlotDefinition GetSlot(int index) =>
        Slots.FirstOrDefault(s => s.SlotIndex == index);

    public (int Col, int Row)? GetSlotGridCell(int slotIndex)
    {
        var s = GetSlot(slotIndex);
        return s == null ? null : (s.GridCol, s.GridRow);
    }

    /// <summary>
    /// Returns true when the given small slot's pair has a large-type plot assigned,
    /// meaning this slot is physically consumed by that spanning plot.
    /// </summary>
    public bool IsSmallSlotConsumedByPair(int slotIndex, IEnumerable<IslandPlot> plots)
    {
        var slot = GetSlot(slotIndex);
        if (slot is not { IsLarge: false } || !slot.PairedSmallSlotIndex.HasValue) return false;
        return plots.Any(p => p.MapSlotIndex == slot.PairedSmallSlotIndex.Value && p.IsLargePlotType());
    }

    /// <summary>
    /// For a small slot with a spanning large-type plot, returns the midpoint between this slot
    /// and its paired slot. Falls back to the slot's own coords if no pair is defined.
    /// </summary>
    public (double X, double Y) GetSpanningSlotCenter(IslandSlotDefinition slot)
    {
        if (slot.PairedSmallSlotIndex.HasValue)
        {
            var paired = GetSlot(slot.PairedSmallSlotIndex.Value);
            if (paired != null)
                return ((slot.X + paired.X) / 2.0, (slot.Y + paired.Y) / 2.0);
        }
        return (slot.X, slot.Y);
    }

public int? WorldToNearestSlot(float wx, float wy, bool? requireLarge = null)
    {
        if (WorldTransform is not { } t || Slots is not { Count: > 0 }) return null;
        var px = t.A * wx + t.B * wy + t.C;
        var py = t.D * wx + t.E * wy + t.F;
        var candidates = requireLarge.HasValue
            ? Slots.Where(s => s.IsLarge == requireLarge.Value).ToList()
            : (IEnumerable<IslandSlotDefinition>) Slots;
        return candidates.MinBy(s => Math.Pow(s.X - px, 2) + Math.Pow(s.Y - py, 2))?.SlotIndex;
    }
}

public static class IslandLayouts
{
    public const string PlayerStandard = "player-standard";
    public const string PlayerMists = "player-mists";
    public const string GuildStandard = "guild-standard";

    // Slot pixel coords at 1024×1024 resolution (mirrored PNGs, left-right flipped).
    // 16 large plots (IsLarge=true) + 2 small plots (IsLarge=false) = 18 total per island.
    // Coords detected via SimpleBlobDetector cross-skin clustering, then X mirrored (1024-x).
    // All city skins share identical layout — only texture differs.
    // SlotIndex is sequential placeholder; remap to game indices once network packets confirm them.
    private static readonly IslandLayoutDefinition _playerStandard = new()
    {
        LayoutId = PlayerStandard,
        DisplayName = "Player Island (Standard)",
        MapImageResourcePath = "pack://application:,,,/Assets/IslandMaps/ISLAND-PLAYER-STEPPE-0001f_ISL_ST_AUTO_T1_NON.png",
        GridColumns = 6,
        GridRows = 5,
        WorldTransform = (A: 2.845, B: -0.045, C: -68.0, D: -0.027, E: -2.773, F: 823.0),
        Slots = new List<IslandSlotDefinition>
        {
            // Large plots — numbered top-left → bottom-right (spatial order)
            new() { SlotIndex = 1,  X = 356, Y = 609, IsLarge = true,  GridCol = 1, GridRow = 4 },
            new() { SlotIndex = 2,  X = 329, Y = 552, IsLarge = true,  GridCol = 1, GridRow = 3 },
            new() { SlotIndex = 3,  X = 241, Y = 554, IsLarge = true,  GridCol = 0, GridRow = 3 },
            new() { SlotIndex = 4,  X = 217, Y = 496, IsLarge = true,  GridCol = 0, GridRow = 2 },
            new() { SlotIndex = 5,  X = 326, Y = 439, IsLarge = true,  GridCol = 1, GridRow = 1 },
            new() { SlotIndex = 6,  X = 441, Y = 498, IsLarge = true,  GridCol = 3, GridRow = 2 },
            new() { SlotIndex = 7,  X = 413, Y = 410, IsLarge = true,  GridCol = 2, GridRow = 1 },
            new() { SlotIndex = 8,  X = 471, Y = 410, IsLarge = true,  GridCol = 3, GridRow = 1 },
            new() { SlotIndex = 9,  X = 474, Y = 351, IsLarge = true,  GridCol = 2, GridRow = 0 },
            new() { SlotIndex = 10, X = 528, Y = 351, IsLarge = true,  GridCol = 3, GridRow = 0 },
            new() { SlotIndex = 11, X = 554, Y = 466, IsLarge = true,  GridCol = 4, GridRow = 1 },
            new() { SlotIndex = 12, X = 696, Y = 466, IsLarge = true,  GridCol = 5, GridRow = 1 },
            new() { SlotIndex = 13, X = 754, Y = 551, IsLarge = true,  GridCol = 5, GridRow = 3 },
            new() { SlotIndex = 14, X = 671, Y = 551, IsLarge = true,  GridCol = 4, GridRow = 3 },
            new() { SlotIndex = 15, X = 554, Y = 551, IsLarge = true,  GridCol = 3, GridRow = 3 },
            new() { SlotIndex = 16, X = 498, Y = 607, IsLarge = true,  GridCol = 3, GridRow = 4 },
            // Small plots — S1/S2 (paired: a large-type plot on one consumes the other)
            new() { SlotIndex = 17, X = 339, Y = 510, IsLarge = false, GridCol = 2, GridRow = 2, PairedSmallSlotIndex = 18 },
            new() { SlotIndex = 18, X = 312, Y = 510, IsLarge = false, GridCol = 1, GridRow = 2, PairedSmallSlotIndex = 17 },
        }
    };

    // Mists islands use a distinct layout (purple palette, different arrangement).
    // Coords detected via SimpleBlobDetector, then X mirrored (1024-x).
    // SlotIndex is sequential placeholder; remap to game indices once network packets confirm them.
    // GridColumns/GridRows and per-slot GridCol/GridRow omitted intentionally — mists slot grid
    // positions are unconfirmed. Populate once game packets map slot indices to grid cells.
    // WorldTransform calibrated from live Brecilien packet capture (2026-06-02):
    //   16 large plot centers from T1_FARMHOUSE/T5_PLAYERHOUSE NewBuilding events, Hungarian-matched
    //   to pixel coords — mean residual 2.4px, max 6.6px.
    //   Small plots (SlotIndex 17–18) were unpopulated; world coords unknown.
    private static readonly IslandLayoutDefinition _playerMists = new()
    {
        LayoutId = PlayerMists,
        DisplayName = "Player Island (Mists)",
        MapImageResourcePath = "pack://application:,,,/Assets/IslandMaps/ISLAND-PLAYER-MISTS-0001f_ISL_MI_AUTO_T1_NON.png",
        WorldTransform = (A: 3.8212, B: 0.0030, C: -251.46, D: 0.0090, E: -3.8049, F: 906.43),
        Slots = new List<IslandSlotDefinition>
        {
            // Large plots — indexed 1..16
            new() { SlotIndex = 1,  X = 418, Y = 278, IsLarge = true  },
            new() { SlotIndex = 2,  X = 498, Y = 280, IsLarge = true  },
            new() { SlotIndex = 3,  X = 530, Y = 358, IsLarge = true  },
            new() { SlotIndex = 4,  X = 456, Y = 358, IsLarge = true  },
            new() { SlotIndex = 5,  X = 377, Y = 358, IsLarge = true  },
            new() { SlotIndex = 6,  X = 226, Y = 394, IsLarge = true  },
            new() { SlotIndex = 7,  X = 798, Y = 465, IsLarge = true  },
            new() { SlotIndex = 8,  X = 226, Y = 468, IsLarge = true  },
            new() { SlotIndex = 9,  X = 455, Y = 470, IsLarge = true  },
            new() { SlotIndex = 10, X = 611, Y = 475, IsLarge = true  },
            new() { SlotIndex = 11, X = 839, Y = 551, IsLarge = true  },
            new() { SlotIndex = 12, X = 761, Y = 585, IsLarge = true  },
            new() { SlotIndex = 13, X = 608, Y = 585, IsLarge = true  },
            new() { SlotIndex = 14, X = 304, Y = 585, IsLarge = true  },
            new() { SlotIndex = 15, X = 531, Y = 622, IsLarge = true  },
            new() { SlotIndex = 16, X = 381, Y = 622, IsLarge = true  },
            // Small plots — indexed 17..18
            new() { SlotIndex = 17, X = 630, Y = 416, IsLarge = false },
            new() { SlotIndex = 18, X = 590, Y = 416, IsLarge = false },
        }
    };

    // TODO: replace coords once guild island PNGs confirmed.
    // All guild city skins share one layout.
    private static readonly IslandLayoutDefinition _guildStandard = new()
    {
        LayoutId = GuildStandard,
        DisplayName = "Guild Island (Standard)",
        MapImageResourcePath = string.Empty,
        Slots = new List<IslandSlotDefinition>
        {
            new() { SlotIndex = 1,  X = 110, Y = 170, IsLarge = true  },
            new() { SlotIndex = 2,  X = 170, Y = 140, IsLarge = true  },
            new() { SlotIndex = 3,  X = 235, Y = 115, IsLarge = true  },
            new() { SlotIndex = 4,  X = 300, Y = 140, IsLarge = true  },
            new() { SlotIndex = 5,  X = 365, Y = 170, IsLarge = true  },
            new() { SlotIndex = 6,  X = 110, Y = 235, IsLarge = true  },
            new() { SlotIndex = 7,  X = 170, Y = 205, IsLarge = true  },
            new() { SlotIndex = 8,  X = 300, Y = 205, IsLarge = true  },
            new() { SlotIndex = 9,  X = 365, Y = 235, IsLarge = true  },
            new() { SlotIndex = 10, X = 110, Y = 300, IsLarge = true  },
            new() { SlotIndex = 11, X = 170, Y = 270, IsLarge = true  },
            new() { SlotIndex = 12, X = 300, Y = 270, IsLarge = true  },
            new() { SlotIndex = 13, X = 365, Y = 300, IsLarge = true  },
            new() { SlotIndex = 14, X = 170, Y = 340, IsLarge = true  },
            new() { SlotIndex = 15, X = 235, Y = 365, IsLarge = true  },
            new() { SlotIndex = 16, X = 300, Y = 340, IsLarge = true  },
            new() { SlotIndex = 17, X = 235, Y = 240, IsLarge = false },
            new() { SlotIndex = 18, X = 265, Y = 240, IsLarge = false },
        }
    };

    private static readonly Dictionary<string, IslandLayoutDefinition> _all = new()
    {
        [PlayerStandard] = _playerStandard,
        [PlayerMists] = _playerMists,
        [GuildStandard] = _guildStandard,
    };

    private static readonly Dictionary<string, string> _cityToImagePath = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bridgewatch"]  = "pack://application:,,,/Assets/IslandMaps/ISLAND-PLAYER-STEPPE-0001f_ISL_ST_AUTO_T1_NON.png",
        ["Lymhurst"]     = "pack://application:,,,/Assets/IslandMaps/ISLAND-PLAYER-FOREST-0001f_ISL_FR_AUTO_T1_NON.png",
        ["Martlock"]     = "pack://application:,,,/Assets/IslandMaps/ISLAND-PLAYER-HIGHLAND-0001f_ISL_HL_AUTO_T1_NON.png",
        ["Fort Sterling"] = "pack://application:,,,/Assets/IslandMaps/ISLAND-PLAYER-MOUNTAIN-0001f_ISL_MN_AUTO_T1_NON.png",
        ["Thetford"]     = "pack://application:,,,/Assets/IslandMaps/ISLAND-PLAYER-SWAMP-0001f_ISL_SW_AUTO_T1_NON.png",
        ["Caerleon"]     = "pack://application:,,,/Assets/IslandMaps/ISLAND-PLAYER-CAERLEON-0001f_ISL_HL_DEAD_T1_NON.png",
        ["Brecilien"]    = "pack://application:,,,/Assets/IslandMaps/ISLAND-PLAYER-MISTS-0001f_ISL_MI_AUTO_T1_NON.png",
    };

    public static string FormatSlotLabel(int slotIndex) => slotIndex switch
    {
        17 => "S1",
        18 => "S2",
        _ => $"#{slotIndex}"
    };

    public static IReadOnlyCollection<IslandLayoutDefinition> All => _all.Values;

    public static IslandLayoutDefinition Get(string layoutId) =>
        _all.TryGetValue(layoutId ?? string.Empty, out var def) ? def : null;

    /// <summary>
    /// Resolves the layout and city-specific map image for a given island.
    /// Returns (null, null) when no map is available (e.g. guild islands).
    /// </summary>
    public static (IslandLayoutDefinition Layout, string ImagePath) ResolveForIsland(IslandType islandType, string city)
    {
        if (islandType == IslandType.Guild)
            return (null, null);

        var layout = string.Equals(city, "Brecilien", StringComparison.OrdinalIgnoreCase)
            ? _playerMists
            : _playerStandard;

        _cityToImagePath.TryGetValue(city ?? string.Empty, out var imagePath);
        return (layout, imagePath ?? string.Empty);
    }
}
