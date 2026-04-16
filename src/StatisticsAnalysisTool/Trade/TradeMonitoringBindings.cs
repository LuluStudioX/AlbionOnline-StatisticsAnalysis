using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Common.UserSettings;
using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.GameFileData;
using StatisticsAnalysisTool.Localization;
using StatisticsAnalysisTool.Models;
using StatisticsAnalysisTool.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace StatisticsAnalysisTool.Trade;

public class TradeMonitoringBindings : BaseViewModel
{
    private readonly record struct TradeFilterContext(long FromTicks, long ToTicks, string SearchText, long? SearchNumber,
        IReadOnlyList<TradeFilterItem> TypeFilters, HashSet<int> TierFilter, HashSet<int> LevelFilter,
        string CategoryId, string SubCategory1Id, string SubCategory2Id, string SubCategory3Id);

    public List<TradeFilterItem> TradeTypeFilterItems { get; } = new()
    {
        new() { DisplayName = "Bought",       Matches = t => t.Type == TradeType.Mail && t.MailType == MailType.MarketplaceBuyOrderFinished },
        new() { DisplayName = "Sold",         Matches = t => t.Type == TradeType.Mail && t.MailType == MailType.MarketplaceSellOrderFinished },
        new() { DisplayName = "Buy Expired",  Matches = t => t.Type == TradeType.Mail && t.MailType == MailType.MarketplaceBuyOrderExpired },
        new() { DisplayName = "Sell Expired", Matches = t => t.Type == TradeType.Mail && t.MailType == MailType.MarketplaceSellOrderExpired },
        new() { DisplayName = "Instant Sell", Matches = t => t.Type == TradeType.InstantSell },
        new() { DisplayName = "Instant Buy",  Matches = t => t.Type == TradeType.InstantBuy },
        new() { DisplayName = "Manual Sell",  Matches = t => t.Type == TradeType.ManualSell },
        new() { DisplayName = "Manual Buy",   Matches = t => t.Type == TradeType.ManualBuy },
        new() { DisplayName = "Crafting",     Matches = t => t.Type == TradeType.Crafting },
    };

    public List<TradeFilterItem<int>> TierFilterItems { get; } = new()
    {
        new() { Value = 1, DisplayName = "T1" }, new() { Value = 2, DisplayName = "T2" },
        new() { Value = 3, DisplayName = "T3" }, new() { Value = 4, DisplayName = "T4" },
        new() { Value = 5, DisplayName = "T5" }, new() { Value = 6, DisplayName = "T6" },
        new() { Value = 7, DisplayName = "T7" }, new() { Value = 8, DisplayName = "T8" },
    };

    public List<TradeFilterItem<int>> LevelFilterItems { get; } = new()
    {
        new() { Value = 0, DisplayName = ".0" }, new() { Value = 1, DisplayName = ".1" },
        new() { Value = 2, DisplayName = ".2" }, new() { Value = 3, DisplayName = ".3" },
        new() { Value = 4, DisplayName = ".4" },
    };
    private ListCollectionView _tradeCollectionView;
    private ObservableRangeCollection<Trade> _trades = new();
    private string _tradesSearchText;
    private DateTime _datePickerTradeFrom = new(2017, 1, 1);
    private DateTime _datePickerTradeTo = DateTime.UtcNow.AddDays(1);
    private TradeStatsObject _tradeStatsObject = new();
    private TradeOptionsObject _tradeOptionsObject = new();
    private Visibility _isTradeMonitoringPopupVisible = Visibility.Collapsed;
    private GridLength _gridSplitterPosition = GridLength.Auto;
    private int _totalTradeCounts;
    private int _currentTradeCounts;
    private ManuallyTradeMenuObject _tradeManuallyMenuObject = new();
    private bool _isDeleteTradesButtonEnabled = true;
    private Visibility _filteringIsRunningIconVisibility = Visibility.Collapsed;
    private TradeExportTemplateObject _tradeExportTemplateObject = new();

    // Category filter backing fields
    private ObservableCollection<CategoryDropdownItem> _categoryItems = [];
    private ObservableCollection<CategoryDropdownItem> _subCategory1Items = [];
    private ObservableCollection<CategoryDropdownItem> _subCategory2Items = [];
    private ObservableCollection<CategoryDropdownItem> _subCategory3Items = [];
    private CategoryDropdownItem _selectedCategory;
    private CategoryDropdownItem _selectedSubCategory1;
    private CategoryDropdownItem _selectedSubCategory2;
    private CategoryDropdownItem _selectedSubCategory3;
    public TradeMonitoringBindings()
    {
        foreach (var item in TradeTypeFilterItems)
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(TradeTypeSummary));
        foreach (var item in TierFilterItems)
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(TierSummary));
        foreach (var item in LevelFilterItems)
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(LevelSummary));

        TradeCollectionView = CollectionViewSource.GetDefaultView(Trades) as ListCollectionView;

        if (TradeCollectionView != null)
        {
            Trades.CollectionChanged += UpdateTotalTradesUi;
            TradeCollectionView.CurrentChanged += UpdateCurrentTradesUi;

            TradeCollectionView.IsLiveSorting = true;
            TradeCollectionView.IsLiveFiltering = true;
            TradeCollectionView.CustomSort = new TradeComparer();
            TradeCollectionView.Refresh();
        }

        DatePickerTradeFrom = SettingsController.CurrentSettings.TradeMonitoringDatePickerTradeFrom;
        DatePickerTradeTo = SettingsController.CurrentSettings.TradeMonitoringDatePickerTradeTo;

        LoadCategoryItems();
    }

    private void LoadCategoryItems()
    {
        var items = ItemController.GetRootCategories()
            .OrderBy(c => c.Value, StringComparer.Ordinal)
            .Select(c => new CategoryDropdownItem
            {
                Id = c.Id,
                Value = c.Value,
                DisplayName = LocalizationController.Translation("@MARKETPLACEGUI_ROLLOUT_SHOPCATEGORY_" + c.Id.ToUpperInvariant())
            });
        CategoryItems = new ObservableCollection<CategoryDropdownItem>(items);
    }

    private static ObservableCollection<CategoryDropdownItem> ToDropdownItems(IEnumerable<(string Id, string Value)> source, string translationPrefix)
    {
        if (source == null) return [];
        return new ObservableCollection<CategoryDropdownItem>(
            source.Select(x => new CategoryDropdownItem
            {
                Id = x.Id ?? string.Empty,
                Value = x.Value ?? string.Empty,
                DisplayName = LocalizationController.Translation(translationPrefix + (x.Id ?? "UNKNOWN").ToUpperInvariant()) ?? x.Id
            }));
    }

    public string TradeTypeSummary =>
        TradeTypeFilterItems.Any(x => x.IsSelected)
            ? string.Join(", ", TradeTypeFilterItems.Where(x => x.IsSelected).Select(x => x.DisplayName))
            : "Trade Type";

    public string TierSummary =>
        TierFilterItems.Any(x => x.IsSelected)
            ? string.Join(", ", TierFilterItems.Where(x => x.IsSelected).Select(x => x.DisplayName))
            : "Tier";

    public string LevelSummary =>
        LevelFilterItems.Any(x => x.IsSelected)
            ? string.Join(", ", LevelFilterItems.Where(x => x.IsSelected).Select(x => x.DisplayName))
            : "Enchantment";

    public string CategorySummary => _selectedCategory?.DisplayName ?? "Category";
    public string SubCategory1Summary => _selectedSubCategory1?.DisplayName ?? "Sub-Category";
    public string SubCategory2Summary => _selectedSubCategory2?.DisplayName ?? "Sub-Category 2";
    public string SubCategory3Summary => _selectedSubCategory3?.DisplayName ?? "Sub-Category 3";

    public ObservableCollection<CategoryDropdownItem> CategoryItems
    {
        get => _categoryItems;
        set { _categoryItems = value; OnPropertyChanged(); }
    }

    public CategoryDropdownItem SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (_selectedCategory == value) return;
            _selectedCategory = value;
            SubCategory1Items = ToDropdownItems(ItemController.GetSubCategories1(_selectedCategory?.Id), "@MARKETPLACEGUI_ROLLOUT_SHOPSUBCATEGORY_");
            SelectedSubCategory1 = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CategorySummary));
        }
    }

    public ObservableCollection<CategoryDropdownItem> SubCategory1Items
    {
        get => _subCategory1Items;
        set { _subCategory1Items = value; OnPropertyChanged(); }
    }

    public CategoryDropdownItem SelectedSubCategory1
    {
        get => _selectedSubCategory1;
        set
        {
            if (_selectedSubCategory1 == value) return;
            _selectedSubCategory1 = value;
            if (_selectedSubCategory1 != null && _selectedCategory != null)
                SubCategory2Items = ToDropdownItems(ItemController.GetSubCategories2(_selectedCategory.Id, _selectedSubCategory1.Id), "@MARKETPLACEGUI_ROLLOUT_SHOPSUBCATEGORY_");
            else
                SubCategory2Items = [];
            SelectedSubCategory2 = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubCategory1Summary));
        }
    }

    public ObservableCollection<CategoryDropdownItem> SubCategory2Items
    {
        get => _subCategory2Items;
        set { _subCategory2Items = value; OnPropertyChanged(); }
    }

    public CategoryDropdownItem SelectedSubCategory2
    {
        get => _selectedSubCategory2;
        set
        {
            if (_selectedSubCategory2 == value) return;
            _selectedSubCategory2 = value;
            if (_selectedSubCategory2 != null && _selectedCategory != null && _selectedSubCategory1 != null)
                SubCategory3Items = ToDropdownItems(ItemController.GetSubCategories3(_selectedCategory.Id, _selectedSubCategory1.Id, _selectedSubCategory2.Id), "@MARKETPLACEGUI_ROLLOUT_SHOPSUBCATEGORY_");
            else
                SubCategory3Items = [];
            SelectedSubCategory3 = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubCategory2Summary));
        }
    }

    public ObservableCollection<CategoryDropdownItem> SubCategory3Items
    {
        get => _subCategory3Items;
        set { _subCategory3Items = value; OnPropertyChanged(); }
    }

    public CategoryDropdownItem SelectedSubCategory3
    {
        get => _selectedSubCategory3;
        set
        {
            if (_selectedSubCategory3 == value) return;
            _selectedSubCategory3 = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SubCategory3Summary));
        }
    }

    public void ItemFilterReset()
    {
        _datePickerTradeFrom = new DateTime(2017, 1, 1);
        _datePickerTradeTo = DateTime.UtcNow.AddDays(1);
        TradesSearchText = string.Empty;

        foreach (var item in TradeTypeFilterItems) item.IsSelected = false;
        foreach (var item in TierFilterItems) item.IsSelected = false;
        foreach (var item in LevelFilterItems) item.IsSelected = false;

        SelectedCategory = null;
        // Clearing the selected category cascades: SelectedSubCategory1 → 2 → 3 are cleared automatically.

        TradeCollectionView = CollectionViewSource.GetDefaultView(Trades) as ListCollectionView;
        if (TradeCollectionView != null)
        {
            TradeCollectionView.Filter = null;
        }
    }

    public ListCollectionView TradeCollectionView
    {
        get => _tradeCollectionView;
        set
        {
            _tradeCollectionView = value;
            OnPropertyChanged();
        }
    }

    public ObservableRangeCollection<Trade> Trades
    {
        get => _trades;
        set
        {
            _trades = value;
            OnPropertyChanged();
        }
    }

    public string TradesSearchText
    {
        get => _tradesSearchText;
        set
        {
            _tradesSearchText = value;
            OnPropertyChanged();
        }
    }

    public DateTime DatePickerTradeFrom
    {
        get => _datePickerTradeFrom;
        set
        {
            _datePickerTradeFrom = value;
            SettingsController.CurrentSettings.TradeMonitoringDatePickerTradeFrom = _datePickerTradeFrom;
            OnPropertyChanged();
        }
    }

    public DateTime DatePickerTradeTo
    {
        get => _datePickerTradeTo;
        set
        {
            _datePickerTradeTo = value;
            SettingsController.CurrentSettings.TradeMonitoringDatePickerTradeTo = _datePickerTradeTo;
            OnPropertyChanged();
        }
    }

    public bool IsDeleteTradesButtonEnabled
    {
        get => _isDeleteTradesButtonEnabled;
        set
        {
            _isDeleteTradesButtonEnabled = value;
            OnPropertyChanged();
        }
    }

    public TradeStatsObject TradeStatsObject
    {
        get => _tradeStatsObject;
        set
        {
            _tradeStatsObject = value;
            OnPropertyChanged();
        }
    }

    public ManuallyTradeMenuObject ManuallyTradeMenuObject
    {
        get => _tradeManuallyMenuObject;
        set
        {
            _tradeManuallyMenuObject = value;
            OnPropertyChanged();
        }
    }

    public TradeExportTemplateObject TradeExportTemplateObject
    {
        get => _tradeExportTemplateObject;
        set
        {
            _tradeExportTemplateObject = value;
            OnPropertyChanged();
        }
    }

    public TradeOptionsObject TradeOptionsObject
    {
        get => _tradeOptionsObject;
        set
        {
            _tradeOptionsObject = value;
            OnPropertyChanged();
        }
    }

    public int TotalTradeCounts
    {
        get => _totalTradeCounts;
        set
        {
            _totalTradeCounts = value;
            OnPropertyChanged();
        }
    }

    public int CurrentTradeCounts
    {
        get => _currentTradeCounts;
        set
        {
            _currentTradeCounts = value;
            OnPropertyChanged();
        }
    }

    public Visibility IsTradeMonitoringPopupVisible
    {
        get => _isTradeMonitoringPopupVisible;
        set
        {
            _isTradeMonitoringPopupVisible = value;
            OnPropertyChanged();
        }
    }

    public GridLength GridSplitterPosition
    {
        get => _gridSplitterPosition;
        set
        {
            _gridSplitterPosition = value;
            SettingsController.CurrentSettings.MailMonitoringGridSplitterPosition = _gridSplitterPosition.Value;
            OnPropertyChanged();
        }
    }

    public Visibility FilteringIsRunningIconVisibility
    {
        get => _filteringIsRunningIconVisibility;
        set
        {
            _filteringIsRunningIconVisibility = value;
            OnPropertyChanged();
        }
    }

    #region Update ui

    public void UpdateTotalTradesUi(object sender, NotifyCollectionChangedEventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            TotalTradeCounts = Trades.Count;
        });
    }

    public void UpdateCurrentTradesUi(object sender, EventArgs e)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            CurrentTradeCounts = TradeCollectionView.Count;
        });
    }

    #endregion

    #region Filter

    private CancellationTokenSource _cancellationTokenSource;

    public async Task UpdateFilteredTradesAsync()
    {
        if (Trades?.Count <= 0)
        {
            return;
        }

        FilteringIsRunningIconVisibility = Visibility.Visible;

        if (_cancellationTokenSource is not null)
        {
            await _cancellationTokenSource.CancelAsync();
        }
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var filteredTrades = await Task.Run(ParallelTradeFilterProcess, _cancellationTokenSource.Token);

            if (Trades != null)
            {
                TradeCollectionView ??= CollectionViewSource.GetDefaultView(Trades) as ListCollectionView;
            }

            if (TradeCollectionView != null)
            {
                var filteredTradeSet = filteredTrades.ToHashSet();
                TradeCollectionView.Filter = obj => obj is Trade trade && filteredTradeSet.Contains(trade);

                TradeStatsObject?.SetTradeStats(TradeCollectionView?.Cast<Trade>().ToList());
            }

            UpdateCurrentTradesUi(null, null);
        }
        catch (TaskCanceledException)
        {
            // Ignored
        }
        finally
        {
            FilteringIsRunningIconVisibility = Visibility.Collapsed;
        }
    }

    public List<Trade> ParallelTradeFilterProcess()
    {
        var context = BuildFilterContext();
        var partitioner = Partitioner.Create(Trades, EnumerablePartitionerOptions.NoBuffering);
        var result = new ConcurrentBag<Trade>();

        Parallel.ForEach(partitioner, (tradeBatch, state) =>
        {
            if (_cancellationTokenSource.Token.IsCancellationRequested)
            {
                FilteringIsRunningIconVisibility = Visibility.Collapsed;
                state.Stop();
            }

            if (Filter(tradeBatch, context))
            {
                result.Add(tradeBatch);
            }
        });

        return result.OrderByDescending(d => d.Ticks).ToList();
    }

    private TradeFilterContext BuildFilterContext()
    {
        var searchText = TradesSearchText?.Trim() ?? string.Empty;
        var hasNumericSearch = long.TryParse(searchText, NumberStyles.Any, CultureInfo.CurrentCulture, out var searchNumber);
        var toDate = DatePickerTradeTo.Date;
        var toTicks = toDate == DateTime.MaxValue.Date ? DateTime.MaxValue.Ticks : toDate.AddDays(1).AddTicks(-1).Ticks;

        var typeFilters = TradeTypeFilterItems.Where(x => x.IsSelected).ToList();
        var tiers = TierFilterItems.Where(x => x.IsSelected).Select(x => x.Value).ToHashSet();
        var levels = LevelFilterItems.Where(x => x.IsSelected).Select(x => x.Value).ToHashSet();

        return new TradeFilterContext(DatePickerTradeFrom.Ticks, toTicks, searchText, hasNumericSearch ? searchNumber : null,
            typeFilters, tiers, levels,
            SelectedCategory?.Id, SelectedSubCategory1?.Id, SelectedSubCategory2?.Id, SelectedSubCategory3?.Id);
    }

    private bool Filter(object obj, TradeFilterContext context)
    {
        if (obj is not Trade trade)
        {
            return false;
        }

        if (trade.Ticks < context.FromTicks || trade.Ticks > context.ToTicks)
        {
            return false;
        }

        if (context.TypeFilters.Count > 0 && !context.TypeFilters.Any(f => f.Matches(trade)))
        {
            return false;
        }

        if (context.TierFilter.Count > 0 && !context.TierFilter.Contains(trade.Item?.Tier ?? 0))
        {
            return false;
        }

        if (context.LevelFilter.Count > 0 && !context.LevelFilter.Contains(trade.Item?.Level ?? 0))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(context.CategoryId) && trade.Item?.FullItemInformation?.ShopCategory != context.CategoryId)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(context.SubCategory1Id) && trade.Item?.FullItemInformation?.ShopSubCategory1 != context.SubCategory1Id)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(context.SubCategory2Id) && trade.Item?.FullItemInformation?.ShopSubCategory2 != context.SubCategory2Id)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(context.SubCategory3Id) && trade.Item?.FullItemInformation?.ShopSubCategory3 != context.SubCategory3Id)
        {
            return false;
        }

        if (string.IsNullOrEmpty(context.SearchText))
        {
            return true;
        }

        if (context.SearchNumber is { } searchNumber)
        {
            return trade.MailContent?.UnitPriceWithoutTax.IntegerValue == searchNumber ||
                   trade.MailContent?.TotalPrice.IntegerValue == searchNumber ||
                   trade.InstantBuySellContent?.UnitPrice.IntegerValue == searchNumber ||
                   trade.InstantBuySellContent?.TotalPrice.IntegerValue == searchNumber;
        }

        return (trade.LocationName?.IndexOf(context.SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
               ($"T{trade.Item?.Tier}.{trade.Item?.Level}".IndexOf(context.SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
               (trade.MailTypeDescription?.IndexOf(context.SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
               (trade.Item?.LocalizedName?.IndexOf(context.SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
               (trade.MailContent?.UnitPriceWithoutTax.ToString().IndexOf(context.SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
               (trade.MailContent?.TotalPrice.ToString().IndexOf(context.SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
               (trade.InstantBuySellContent?.UnitPrice.ToString().IndexOf(context.SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
               (trade.InstantBuySellContent?.TotalPrice.ToString().IndexOf(context.SearchText, StringComparison.OrdinalIgnoreCase) >= 0) ||
               (trade.Description?.IndexOf(context.SearchText, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    #endregion
}