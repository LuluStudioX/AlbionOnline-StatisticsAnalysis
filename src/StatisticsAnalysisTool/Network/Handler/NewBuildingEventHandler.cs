using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Manager;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class NewBuildingEventHandler : EventPacketHandler<NewBuildingEvent>
{
    private readonly TrackingController _trackingController;

    public NewBuildingEventHandler(TrackingController trackingController) : base((int) EventCodes.NewBuilding)
    {
        _trackingController = trackingController;
    }

    protected override Task OnActionAsync(NewBuildingEvent value)
    {
        if (ClusterController.CurrentCluster?.MapType == MapType.Island)
        {
            _trackingController.IslandController.HandleNewBuilding(value);
        }
        return Task.CompletedTask;
    }
}
