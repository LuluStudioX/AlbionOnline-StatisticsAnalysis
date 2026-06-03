using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Models;
using StatisticsAnalysisTool.ViewModels;
using System;

namespace StatisticsAnalysisTool.Island;

public class IslandYieldEntry : BaseViewModel
{
    private int _itemIndex;
    private int _quantity;

    public int ItemIndex
    {
        get => _itemIndex;
        set
        {
            _itemIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Item));
            OnPropertyChanged(nameof(TotalAvgEstMarketValue));
        }
    }

    public int Quantity
    {
        get => _quantity;
        set
        {
            _quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalAvgEstMarketValue));
        }
    }

    public DateTime CollectedAt { get; set; }
    public PlotType SourcePlot { get; set; }

    public Item Item => ItemController.GetItemByIndex(ItemIndex);
    public double TotalAvgEstMarketValue => Quantity * (Item?.AverageEstMarketValue ?? 0);
}
