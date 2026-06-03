using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Models;
using StatisticsAnalysisTool.ViewModels;

namespace StatisticsAnalysisTool.Island;

public class IslandYieldAggregateRow : BaseViewModel
{
    private int _itemIndex;
    private long _totalQuantity;
    private string _islandName;

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

    public long TotalQuantity
    {
        get => _totalQuantity;
        set
        {
            _totalQuantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalAvgEstMarketValue));
        }
    }

    public string IslandName
    {
        get => _islandName;
        set
        {
            _islandName = value;
            OnPropertyChanged();
        }
    }

    public Item Item => ItemController.GetItemByIndex(ItemIndex);
    public double TotalAvgEstMarketValue => TotalQuantity * (Item?.AverageEstMarketValue ?? 0);
}
