using System.Collections.Generic;

namespace StatisticsAnalysisTool.Island;

public static class IslandPlotHelper
{
    public static bool ShouldExpand(IslandPlot plot)
    {
        return plot.Quantity > 1;
    }

    public static IEnumerable<IslandPlot> Expand(IslandPlot plot)
    {
        if (!ShouldExpand(plot))
        {
            yield return plot;
            yield break;
        }

        for (var i = 0; i < plot.Quantity; i++)
            yield return new IslandPlot(plot.PlotType, 1, plot.Notes, plot.Configuration);
    }
}
