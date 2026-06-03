using System;

namespace StatisticsAnalysisTool.Island;

[Flags]
public enum ManagerResponsibility
{
    None = 0,
    HandlesRefills = 1,
    NotifyLowResources = 2,
    RequestsMaterials = 4
}
