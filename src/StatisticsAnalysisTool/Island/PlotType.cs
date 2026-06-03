namespace StatisticsAnalysisTool.Island;

public enum PlotType
{
    House,
    Farm,
    Pasture,
    HerbGarden,
    Mill,
    Smelter,
    Tanner,
    Lumbermill,
    Stonemason,
    Butcher,
    Cook,
    AlchemyLab,
    HunterLodge,
    WarriorGuild,
    Kennel,
    Saddler,
    MageTower,
    Weaver,
    Toolmaker,
    RepairStation,
}

public sealed class FarmablePlotInfo
{
    public PlotType PlotType { get; init; }
    public string ConfigKey { get; init; }
    public string DisplayName { get; init; }
    public string UniqueName { get; init; }
}
