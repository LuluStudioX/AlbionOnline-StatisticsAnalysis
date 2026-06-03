using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Manager;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class HarvestFinishedEventHandler : EventPacketHandler<HarvestFinishedEvent>
{
    private readonly TrackingController _trackingController;

    public HarvestFinishedEventHandler(TrackingController trackingController) : base(54) // game event code 54 per packet sniffer (252:54)
    {
        _trackingController = trackingController;
    }

    protected override async Task OnActionAsync(HarvestFinishedEvent value)
    {
        await _trackingController.GatheringController.AddOrUpdateAsync(value.HarvestFinishedObject);
    }
}