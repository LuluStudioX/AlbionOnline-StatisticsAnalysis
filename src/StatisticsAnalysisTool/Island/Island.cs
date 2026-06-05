using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;
using System.Linq;
using StatisticsAnalysisTool.Localization;
using StatisticsAnalysisTool.ViewModels;

namespace StatisticsAnalysisTool.Island;

public enum IslandType
{
    Player,
    Guild,
    Other
}

public class Island : BaseViewModel
{
    private IslandType _islandType = IslandType.Player;
    public IslandType IslandType
    {
        get => _islandType;
        set
        {
            _islandType = value;
            OnPropertyChanged();
        }
    }
    private string _name = string.Empty;
    private string _city = string.Empty;
    private string _owner = string.Empty;
    private int _tier;
    private string _biome = string.Empty;
    private bool _hasPremium;
    private bool _trackingEnabled = true;
    private DateTime _createdDate;
    private DateTime _lastModifiedDate;
    private DateTime? _lastPlantedAt;
    private ObservableCollection<string> _kennelAnimals = new ObservableCollection<string>();
    private ObservableCollection<string> _mountsTaken = new ObservableCollection<string>();
    private bool _collectionReadyNotificationSent;
    private ObservableCollection<IslandPlot> _plots = new ObservableCollection<IslandPlot>();
    private decimal? _managementPayOverride;
    private int? _visitDurationMinutes;
    private string _layoutId = string.Empty;
    private string _worldMapDataType = string.Empty;
    private string _sourceClusterIndex = string.Empty;
    private Dictionary<int, string> _slotLabels = new();
    private DateTime? _lastVisited;
    private DateTime? _lastHandledAt;
    private int _totalLaborersSent;
    private int _totalLootCollected;
    private bool? _mixedRegionAltActive;
    private readonly object _yieldLock = new();

    public Guid Id { get; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DisplayCity));
        }
    }

    public string City
    {
        get => _city;
        set
        {
            _city = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayCity));
        }
    }

    public string Owner
    {
        get => _owner;
        set
        {
            _owner = value;
            OnPropertyChanged();
        }
    }

    public int Tier
    {
        get => _tier;
        set
        {
            _tier = Math.Clamp(value, IslandConstants.IslandMinTier, IslandConstants.IslandMaxTier);
            OnPropertyChanged();
        }
    }

    public string Biome
    {
        get => _biome;
        set
        {
            _biome = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayCityBiome));
        }
    }

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name : DisplayCity;

    [JsonIgnore]
    public string DisplayCity => !string.IsNullOrWhiteSpace(City) ? City : (Name ?? string.Empty);

    [JsonIgnore]
    public string DisplayCityBiome
        => string.IsNullOrWhiteSpace(Biome) ? DisplayCity : $"{DisplayCity} - {Biome}";

    public bool TrackingEnabled
    {
        get => _trackingEnabled;
        set
        {
            _trackingEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool HasPremium
    {
        get => _hasPremium;
        set
        {
            _hasPremium = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CropYieldMultiplier));
            OnPropertyChanged(nameof(FarmingEconomyHint));
            OnPropertyChanged(nameof(MaxCollectionHours));
            OnPropertyChanged(nameof(NextCollectionReadyAt));
            OnPropertyChanged(nameof(CollectionStatusText));
            OnPropertyChanged(nameof(IsCollectionReady));
        }
    }

    public int TotalPlots => _plots?.Count ?? 0;

    public ObservableCollection<IslandPlot> Plots
    {
        get => _plots;
        set
        {
            _plots = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TotalPlots));
        }
    }

    public int? VisitDurationMinutes
    {
        get => _visitDurationMinutes;
        set
        {
            _visitDurationMinutes = value is < 0 ? null : value;
            OnPropertyChanged();
        }
    }

    public string LayoutId
    {
        get => _layoutId;
        set
        {
            _layoutId = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public string WorldMapDataType
    {
        get => _worldMapDataType;
        set
        {
            _worldMapDataType = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public string SourceClusterIndex
    {
        get => _sourceClusterIndex;
        set
        {
            _sourceClusterIndex = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public Dictionary<int, string> SlotLabels
    {
        get => _slotLabels;
        set
        {
            _slotLabels = value ?? new Dictionary<int, string>();
            OnPropertyChanged();
        }
    }

    // Mixed-use region (a large slot that physically shares space with the two small S1/S2 slots).
    // Detected from the house's real position: true = house at the TOP variant (so S1/S2 drop to the
    // bottom), false = house at the BOTTOM (S1/S2 stay top), null = unknown (fall back to occupancy).
    public bool? MixedRegionAltActive
    {
        get => _mixedRegionAltActive;
        set
        {
            _mixedRegionAltActive = value;
            OnPropertyChanged();
        }
    }

    public DateTime? LastVisited
    {
        get => _lastVisited;
        set
        {
            _lastVisited = value;
            OnPropertyChanged();
        }
    }

    public DateTime? LastHandledAt
    {
        get => _lastHandledAt;
        set
        {
            _lastHandledAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DoneToday));
        }
    }

    public int TotalLaborersSent
    {
        get => _totalLaborersSent;
        set
        {
            _totalLaborersSent = Math.Max(0, value);
            OnPropertyChanged();
        }
    }

    public int TotalLootCollected
    {
        get => _totalLootCollected;
        set
        {
            _totalLootCollected = Math.Max(0, value);
            OnPropertyChanged();
        }
    }

    public void SetSlotLabel(int slotIndex, string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            _slotLabels.Remove(slotIndex);
        else
            _slotLabels[slotIndex] = label.Trim();
        OnPropertyChanged(nameof(SlotLabels));
        UpdateModificationDate();
    }

    public string GetSlotLabel(int slotIndex) =>
        _slotLabels.TryGetValue(slotIndex, out var label) ? label : string.Empty;

    public decimal? ManagementPayOverride
    {
        get => _managementPayOverride;
        set
        {
            _managementPayOverride = value is < 0 ? 0 : value;
            OnPropertyChanged();
        }
    }

    public DateTime? LastPlantedAt
    {
        get => _lastPlantedAt;
        set
        {
            _lastPlantedAt = value;
            _collectionReadyNotificationSent = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NextCollectionReadyAt));
            OnPropertyChanged(nameof(CollectionStatusText));
            OnPropertyChanged(nameof(CollectionStatusPrefix));
            OnPropertyChanged(nameof(CollectionStatusSuffix));
            OnPropertyChanged(nameof(IsCollectionReady));
            OnPropertyChanged(nameof(NeedsVisit));
            OnPropertyChanged(nameof(CollectionStatusState));
            OnPropertyChanged(nameof(LastPlantedAtText));
        }
    }

    [JsonIgnore]
    public bool NeedsVisit => IsCollectionReady || !_lastPlantedAt.HasValue;

    [JsonIgnore]
    public bool DoneToday
    {
        get
        {
            if (!_lastHandledAt.HasValue) return false;
            var dt = _lastHandledAt.Value;
            DateTime utc;
            if (dt.Kind == DateTimeKind.Utc)
            {
                utc = dt;
            }
            else if (dt.Kind == DateTimeKind.Local)
            {
                utc = dt.ToUniversalTime();
            }
            else
            {
                // Unspecified — assume local timestamp
                utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
            }

            return utc >= DateTime.UtcNow.AddHours(-26);
        }
    }

    [JsonIgnore]
    public string CollectionStatusPrefix
    {
        get
        {
            var readyAt = NextCollectionReadyAt;
            if (readyAt == null) return string.Empty;

            if (IsCollectionReady)
            {
                return "✓ Ready for collection!";
            }

            return "First ready in";
        }
    }

    [JsonIgnore]
    public string CollectionStatusSuffix
    {
        get
        {
            var readyAt = NextCollectionReadyAt;
            if (readyAt == null) return "Not planted yet";

            if (IsCollectionReady)
            {
                return string.Empty;
            }

            var remaining = readyAt.Value - DateTime.UtcNow;
            var h = (int) remaining.TotalHours;
            var m = remaining.Minutes;
            var firstPart = h > 0 ? $"{h}h {m}m" : $"{m}m";

            var fullAt = FullCollectionReadyAt;
            if (fullAt.HasValue && fullAt.Value > readyAt.Value)
            {
                var remainingFull = fullAt.Value - DateTime.UtcNow;
                var fh = (int) remainingFull.TotalHours;
                var fm = remainingFull.Minutes;
                var fullPart = fh > 0 ? $" (full ready in {fh}h {fm}m)" : $" (full ready in {fm}m)";
                return firstPart + fullPart;
            }

            return firstPart;
        }
    }

    public double MaxCollectionHours
    {
        get
        {
            if (_plots == null || _plots.Count == 0) return 0;
            return _plots.Max(p => p.PlotType.GetBaseCollectionHours(p.Configuration));
        }
    }

    public bool HasAnimalPlots => Plots != null && Plots.Any(p => p.PlotType == PlotType.Pasture && p.Quantity > 0);

    public double FirstCollectionHours
    {
        get
        {
            var max = MaxCollectionHours;
            if (HasAnimalPlots && max > 0)
            {
                return Math.Min(24.0, max);
            }

            return max;
        }
    }

    [JsonIgnore]
    public DateTime? FullCollectionReadyAt
    {
        get
        {
            if (_lastPlantedAt == null) return null;
            DateTime baseUtc = _lastPlantedAt.Value.Kind switch
            {
                DateTimeKind.Utc => _lastPlantedAt.Value,
                DateTimeKind.Local => _lastPlantedAt.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(_lastPlantedAt.Value, DateTimeKind.Local).ToUniversalTime()
            };

            return baseUtc.AddHours(MaxCollectionHours);
        }
    }

    [JsonIgnore]
    public double CropYieldMultiplier => _hasPremium ? 2.0 : 1.0;

    [JsonIgnore]
    public string FarmingEconomyHint => _hasPremium
        ? "Premium: higher crop yield, focus-based seed sustainability, better farming profitability."
        : "Non-Premium: lower crop yield and weaker seed sustainability; many crops can run at a loss.";

    public DateTime? NextCollectionReadyAt
    {
        get
        {
            // Per-plot: take the earliest ready time across all farm plots with known planted timestamps.
            if (_plots is { Count: > 0 })
            {
                DateTime? earliest = null;
                foreach (var plot in _plots.Where(p => p.PlotType.HasCollectionTimer() && p.PlotType != PlotType.House))
                {
                    var planted = plot.PlotPlantedAt;
                    if (!planted.HasValue) continue;
                    var hours = plot.PlotType.GetBaseCollectionHours(plot.Configuration);
                    if (hours <= 0) continue;
                    var readyAt = planted.Value.ToUniversalTime().AddHours(hours);
                    if (!earliest.HasValue || readyAt < earliest.Value)
                        earliest = readyAt;
                }
                if (earliest.HasValue) return earliest.Value;
            }

            // Fallback: island-level LastPlantedAt + shortest cycle.
            if (_lastPlantedAt == null) return null;
            var fallbackHours = FirstCollectionHours;
            if (fallbackHours <= 0) return null;
            DateTime baseUtc = _lastPlantedAt.Value.Kind switch
            {
                DateTimeKind.Utc => _lastPlantedAt.Value,
                DateTimeKind.Local => _lastPlantedAt.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(_lastPlantedAt.Value, DateTimeKind.Local).ToUniversalTime()
            };
            return baseUtc.AddHours(fallbackHours);
        }
    }

    [JsonIgnore]
    public bool IsCollectionReady
        => NextCollectionReadyAt.HasValue && DateTime.UtcNow >= NextCollectionReadyAt.Value;

    public string CollectionStatusText
    {
        get
        {
            var readyAt = NextCollectionReadyAt;
            if (readyAt == null) return LocalizationController.Translation("ISLAND_MANAGEMENT_STATUS_NOT_PLANTED");

            var fullAt = FullCollectionReadyAt;

            if (IsCollectionReady)
            {
                return LocalizationController.Translation("ISLAND_MANAGEMENT_STATUS_COLLECTION_READY");
            }

            var remaining = readyAt.Value - DateTime.UtcNow;
            var h = (int) remaining.TotalHours;
            var m = remaining.Minutes;
            var firstPart = h > 0
                ? string.Format(LocalizationController.Translation("ISLAND_MANAGEMENT_STATUS_FIRST_READY_H_M"), h, m)
                : string.Format(LocalizationController.Translation("ISLAND_MANAGEMENT_STATUS_FIRST_READY_M"), m);

            if (fullAt.HasValue && fullAt.Value > readyAt.Value)
            {
                var remainingFull = fullAt.Value - DateTime.UtcNow;
                var fh = (int) remainingFull.TotalHours;
                var fm = remainingFull.Minutes;
                var fullPart = fh > 0
                    ? string.Format(LocalizationController.Translation("ISLAND_MANAGEMENT_STATUS_FULL_READY_H_M"), fh, fm)
                    : string.Format(LocalizationController.Translation("ISLAND_MANAGEMENT_STATUS_FULL_READY_M"), fm);
                return firstPart + fullPart;
            }

            return firstPart;
        }
    }

    [JsonIgnore]
    public string LastPlantedAtText
        => _lastPlantedAt.HasValue
            ? $"Planted: {_lastPlantedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
            : string.Empty;

    public DateTime CreatedDate
    {
        get => _createdDate;
        set
        {
            _createdDate = value;
            OnPropertyChanged();
        }
    }

    public DateTime LastModifiedDate
    {
        get => _lastModifiedDate;
        set
        {
            _lastModifiedDate = value;
            OnPropertyChanged();
        }
    }

    public Island()
    {
        Id = Guid.NewGuid();
        CreatedDate = DateTime.UtcNow;
        LastModifiedDate = DateTime.UtcNow;
        Plots = new ObservableCollection<IslandPlot>();
        _city = string.Empty;
        _biome = string.Empty;
        KennelAnimals = new ObservableCollection<string>();
        MountsTaken = new ObservableCollection<string>();
        IslandType = IslandType.Player;
    }

    public Island(string name, string owner, int tier, string biome = "", bool hasPremium = false, string city = "", IslandType islandType = IslandType.Other) : this()
    {
        Name = name;
        Owner = owner;
        Tier = tier;
        Biome = biome;
        HasPremium = hasPremium;
        City = city;
        IslandType = islandType;
    }

    public ObservableCollection<string> KennelAnimals
    {
        get => _kennelAnimals;
        set
        {
            _kennelAnimals = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<string> MountsTaken
    {
        get => _mountsTaken;
        set
        {
            _mountsTaken = value;
            OnPropertyChanged();
        }
    }

    public void AddKennelAnimal(string animal)
    {
        if (string.IsNullOrWhiteSpace(animal)) return;
        KennelAnimals.Add(animal);
        UpdateModificationDate();
    }

    public void RemoveKennelAnimal(string animal)
    {
        if (string.IsNullOrWhiteSpace(animal)) return;
        KennelAnimals.Remove(animal);
        UpdateModificationDate();
    }

    public void MarkMountTaken(string mount)
    {
        if (string.IsNullOrWhiteSpace(mount)) return;
        if (!MountsTaken.Contains(mount)) MountsTaken.Add(mount);
        UpdateModificationDate();
    }

    public void MarkMountReturned(string mount)
    {
        if (string.IsNullOrWhiteSpace(mount)) return;
        MountsTaken.Remove(mount);
        UpdateModificationDate();
    }

    public void AddPlot(IslandPlot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);
        foreach (var expanded in IslandPlotHelper.Expand(plot))
            AddSinglePlot(expanded);
    }

    public void AddPlots(IEnumerable<IslandPlot> plots)
    {
        ArgumentNullException.ThrowIfNull(plots);
        foreach (var plot in plots)
            AddPlot(plot);
    }

    private void AddSinglePlot(IslandPlot plot)
    {
        plot.PlotNumber = Plots.Count + 1;
        Plots.Add(plot);
        LastModifiedDate = DateTime.UtcNow;
        OnPropertyChanged(nameof(TotalPlots));
        OnPropertyChanged(nameof(MaxCollectionHours));
    }

    public void RemovePlot(IslandPlot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);
        Plots.Remove(plot);
        RenumberPlots();
        LastModifiedDate = DateTime.UtcNow;
        OnPropertyChanged(nameof(TotalPlots));
        OnPropertyChanged(nameof(MaxCollectionHours));
    }

    private void RenumberPlots()
    {
        for (var i = 0; i < Plots.Count; i++)
            Plots[i].PlotNumber = i + 1;
    }

    public void UpdateModificationDate()
    {
        LastModifiedDate = DateTime.UtcNow;
    }

    public void PlantAll()
    {
        var now = DateTime.UtcNow;
        LastPlantedAt = now;
        LastHandledAt = now;
        if (Plots != null)
        {
            foreach (var plot in Plots.Where(p => p.PlotType.HasCollectionTimer() && p.PlotType != PlotType.House))
                plot.PlotPlantedAt = now;
        }
        UpdateModificationDate();
    }

    public void RefreshTimerDisplay()
    {
        OnPropertyChanged(nameof(CollectionStatusText));
        OnPropertyChanged(nameof(CollectionStatusPrefix));
        OnPropertyChanged(nameof(CollectionStatusSuffix));
        OnPropertyChanged(nameof(IsCollectionReady));
        OnPropertyChanged(nameof(NeedsVisit));
        OnPropertyChanged(nameof(DoneToday));
        OnPropertyChanged(nameof(CollectionStatusState));
        OnPropertyChanged(nameof(NextCollectionReadyAt));
    }

    [JsonIgnore]
    public string CollectionStatusState
    {
        get
        {
            if (IsCollectionReady) return "ready";
            if (DoneToday) return "planted";
            return "default";
        }
    }

    public bool TryMarkCollectionReadyNotification()
    {
        if (!IsCollectionReady)
        {
            _collectionReadyNotificationSent = false;
            return false;
        }

        if (_collectionReadyNotificationSent)
        {
            return false;
        }

        _collectionReadyNotificationSent = true;
        return true;
    }

    public List<IslandYieldEntry> YieldHistory { get; } = [];
    public List<IslandConsumedEntry> ConsumedHistory { get; } = [];

    public void ClearYield()
    {
        lock (_yieldLock)
        {
            YieldHistory.Clear();
            ConsumedHistory.Clear();
        }
    }

    public void AddYield(int itemIndex, int quantity, PlotType source)
    {
        if (itemIndex <= 0 || quantity <= 0)
            return;

        lock (_yieldLock)
        {
            var existing = YieldHistory.FirstOrDefault(e => e.ItemIndex == itemIndex && e.SourcePlot == source);
            if (existing != null)
                existing.Quantity += quantity;
            else
                YieldHistory.Add(new IslandYieldEntry
                {
                    ItemIndex = itemIndex,
                    Quantity = quantity,
                    CollectedAt = DateTime.UtcNow,
                    SourcePlot = source
                });
        }
    }

    public void AddConsumed(int itemIndex, int quantity, PlotType source)
    {
        if (itemIndex <= 0 || quantity <= 0)
            return;

        lock (_yieldLock)
        {
            var existing = ConsumedHistory.FirstOrDefault(e => e.ItemIndex == itemIndex && e.SourcePlot == source);
            if (existing != null)
                existing.Quantity += quantity;
            else
                ConsumedHistory.Add(new IslandConsumedEntry
                {
                    ItemIndex = itemIndex,
                    Quantity = quantity,
                    ConsumedAt = DateTime.UtcNow,
                    SourcePlot = source
                });
        }
    }
}
