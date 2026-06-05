using StatisticsAnalysisTool.EstimatedMarketValue;
using StatisticsAnalysisTool.Network.Events;
using StatisticsAnalysisTool.Network.Manager;
using System.Threading.Tasks;

namespace StatisticsAnalysisTool.Network.Handler;

public class NewLaborerItemEventHandler : EventPacketHandler<NewLaborerItemEvent>
{
    private readonly TrackingController _trackingController;

    public NewLaborerItemEventHandler(TrackingController trackingController) : base(32) // game event code 32 per packet sniffer (252:32)
    {
        _trackingController = trackingController;
    }

    protected override Task OnActionAsync(NewLaborerItemEvent value)
    {
        if (value.Item == null) return Task.CompletedTask;

        if (_trackingController.IsTrackingAllowedByMainCharacter())
        {
            _trackingController.VaultController.AddDiscoveredItem(value.Item);
        }

        EstimatedMarketValueController.Add(value.Item.ItemIndex, value.Item.EstimatedMarketValueInternal, value.Item.Quality);
        _trackingController.LootController.AddDiscoveredItem(value.Item);
        _trackingController.DungeonController.AddDiscoveredItem(value.Item);
        _trackingController.IslandController.HandleLaborerItemDetail(value.Item);
        return Task.CompletedTask;
    }
}