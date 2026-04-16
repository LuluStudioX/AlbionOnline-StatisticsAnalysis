using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.ViewModels;
using System;

namespace StatisticsAnalysisTool.Trade;

public abstract class TradeFilterItemBase : BaseViewModel
{
    private bool _isSelected;

    public string DisplayName { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }
}

// Used for Trade Type filter — each entry carries a predicate so Mail sub-types can be expressed
public class TradeFilterItem : TradeFilterItemBase
{
    public required Func<Trade, bool> Matches { get; init; }
}

// Used for Tier / Level filters where a plain value equality check suffices
public class TradeFilterItem<T> : TradeFilterItemBase
{
    public T Value { get; init; }
}
