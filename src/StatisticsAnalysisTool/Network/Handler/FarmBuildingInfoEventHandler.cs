using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Manager;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class FarmBuildingInfoEventHandler : EventPacketHandler<FarmBuildingInfoEvent>
{
    private readonly TrackingController _trackingController;

    public FarmBuildingInfoEventHandler(TrackingController trackingController) : base((int) EventCodes.FarmBuildingInfo)
    {
        _trackingController = trackingController;
    }

    protected override Task OnActionAsync(FarmBuildingInfoEvent value)
    {
        if (ClusterController.CurrentCluster?.MapType == MapType.Island)
        {
            _trackingController.IslandController.HandleFarmBuildingInfo(value);
        }
        return Task.CompletedTask;
    }
}
