using StatisticsAnalysisTool.Cluster;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Manager;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class FarmableObjectInfoEventHandler : EventPacketHandler<FarmableObjectInfoEvent>
{
    private readonly TrackingController _trackingController;

    public FarmableObjectInfoEventHandler(TrackingController trackingController) : base((int) EventCodes.FarmableObjectInfo)
    {
        _trackingController = trackingController;
    }

    protected override Task OnActionAsync(FarmableObjectInfoEvent value)
    {
        if (ClusterController.CurrentCluster?.MapType == MapType.Island)
        {
            _trackingController.IslandController.HandleFarmableObjectInfo(value);
        }
        return Task.CompletedTask;
    }
}
