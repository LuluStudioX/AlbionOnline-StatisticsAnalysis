using StatisticsAnalysisTool.Common;
using StatisticsAnalysisTool.Island;
using StatisticsAnalysisTool.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Collections.ObjectModel;

namespace StatisticsAnalysisTool.Views;

public partial class AddEditPlotWindow : Window
{
    private static readonly string[] LaborerTypes =
    [
        "None",
        "Blacksmith",
        "Cropper",
        "Fisherman",
        "Fletcher",
        "Gamekeeper",
        "Hunter",
        "Imbuer",
        "Lumberjack",
        "Mage",
        "Mercenary",
        "Prospector",
        "Stonecutter",
        "Tinker",
    ];

    private readonly IslandPlot _existingPlot;
    private readonly List<PlotType> _allTypes;
    private string _pendingFarmableValue = string.Empty;

    public IslandPlot Result { get; private set; }

    public AddEditPlotWindow(IslandPlot existingPlot = null)
    {
        InitializeComponent();

        _existingPlot = existingPlot;
        _allTypes = Enum.GetValues<PlotType>().ToList();

        PopulatePlotTypes();
        PopulateLaborerCombos();

        if (existingPlot != null)
        {
            TitleText.Text = LocalizationController.Translation("ISLAND_MANAGEMENT_EDIT_PLOT");
            TitleIcon.Icon = FontAwesome5.EFontAwesomeIcon.Solid_Edit;
            ConfirmText.Text = LocalizationController.Translation("ISLAND_MANAGEMENT_DIALOG_SAVE_CHANGES");
            ConfirmIcon.Icon = FontAwesome5.EFontAwesomeIcon.Solid_Save;

            var idx = _allTypes.IndexOf(existingPlot.PlotType);
            PlotTypeComboBox.SelectedIndex = Math.Max(0, idx);
            QuantityTextBox.Text = existingPlot.Quantity.ToString();
            ConfigurationTextBox.Text = existingPlot.Configuration;
            NotesTextBox.Text = existingPlot.Notes;

            if (existingPlot.PlotType == PlotType.House)
                LoadLaborerSlotsFromConfig(existingPlot.Configuration);
            else if (existingPlot.PlotType.HasFarmableConfig())
                _pendingFarmableValue = LaborerConfigHelper.ParseConfiguration(existingPlot.Configuration)
                    .TryGetValue(existingPlot.PlotType.GetFarmableConfigKey(), out var val) ? val : string.Empty;
        }
        else
        {
            PlotTypeComboBox.SelectedIndex = 0;
        }

        UpdateConfigVisibility();
    }

    private void PopulatePlotTypes()
    {
        foreach (var pt in _allTypes)
            PlotTypeComboBox.Items.Add(pt.GetDisplayName());
    }

    private void PopulateLaborerCombos()
    {
        // Professions come from the game's laborer-contract data (localized, never hardcoded). Fall back
        // to the static list only if item data has not loaded yet.
        var professions = IslandLaborerProfessions.AllProfessions();
        var options = professions.Count > 0
            ? new[] { LaborerConfigHelper.NoneValue }.Concat(professions)
            : LaborerTypes;

        foreach (var combo in new[] { Laborer1ComboBox, Laborer2ComboBox, Laborer3ComboBox })
        {
            combo.Items.Clear();
            foreach (var t in options)
                combo.Items.Add(t);
            combo.SelectedIndex = 0;
        }
    }

    private void LoadLaborerSlotsFromConfig(string configuration)
    {
        var dict = LaborerConfigHelper.ParseConfiguration(configuration);
        var combos = new[] { Laborer1ComboBox, Laborer2ComboBox, Laborer3ComboBox };
        for (var slot = 1; slot <= 3; slot++)
        {
            var key = LaborerConfigHelper.LaborerKey(slot);
            if (dict.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw)
                && !raw.Equals(LaborerConfigHelper.NoneValue, StringComparison.OrdinalIgnoreCase))
            {
                var display = IslandLaborerProfessions.GetProfession(raw);
                var match = combos[slot - 1].Items.Cast<string>()
                    .FirstOrDefault(i => i.Equals(display, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    combos[slot - 1].SelectedItem = match;
            }
        }
    }

    private PlotType GetSelectedPlotType()
    {
        var idx = PlotTypeComboBox.SelectedIndex;
        return idx >= 0 && idx < _allTypes.Count ? _allTypes[idx] : PlotType.House;
    }

    private void UpdateConfigVisibility()
    {
        var pt = GetSelectedPlotType();
        var isHouse = pt == PlotType.House;
        var isFarmable = pt.HasFarmableConfig();

        LaborerSlotsPanel.Visibility = isHouse ? Visibility.Visible : Visibility.Collapsed;
        FarmableConfigPanel.Visibility = isFarmable ? Visibility.Visible : Visibility.Collapsed;
        RawConfigPanel.Visibility = (!isHouse && !isFarmable) ? Visibility.Visible : Visibility.Collapsed;

        if (isFarmable)
            PopulateFarmableCombo(pt);
    }

    private void PopulateFarmableCombo(PlotType pt)
    {
        FarmableTypeComboBox.Items.Clear();
        FarmableTypeComboBox.Items.Add(new FarmableComboItem(null, "None"));

        FarmableConfigLabel.Text = pt.GetFarmableConfigKey() switch
        {
            "CropType" => LocalizationController.Translation("ISLAND_MANAGEMENT_FARMABLE_CROP_TYPE"),
            "AnimalType" => LocalizationController.Translation("ISLAND_MANAGEMENT_FARMABLE_ANIMAL_TYPE"),
            "MountType" => LocalizationController.Translation("ISLAND_MANAGEMENT_FARMABLE_MOUNT_TYPE"),
            _ => LocalizationController.Translation("ISLAND_MANAGEMENT_DIALOG_FARMABLE_TYPE_LABEL")
        };

        foreach (var opt in pt.GetFarmableOptions())
            FarmableTypeComboBox.Items.Add(new FarmableComboItem(opt.UniqueName, opt.DisplayName));

        if (!string.IsNullOrWhiteSpace(_pendingFarmableValue))
        {
            var match = FarmableTypeComboBox.Items.Cast<FarmableComboItem>()
                .FirstOrDefault(i => i.DisplayName.Equals(_pendingFarmableValue, StringComparison.OrdinalIgnoreCase));
            FarmableTypeComboBox.SelectedItem = match ?? FarmableTypeComboBox.Items[0];
            _pendingFarmableValue = string.Empty;
        }
        else
        {
            FarmableTypeComboBox.SelectedIndex = 0;
        }
    }

    private string BuildFarmableConfiguration(PlotType pt)
    {
        var selected = FarmableTypeComboBox.SelectedItem as FarmableComboItem;
        if (selected?.UniqueName == null || selected.DisplayName.Equals("None", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        var key = pt.GetFarmableConfigKey();
        return LaborerConfigHelper.BuildConfiguration(new Dictionary<string, string> { [key] = selected.DisplayName });
    }

    private string BuildHouseConfiguration()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var combos = new[] { Laborer1ComboBox, Laborer2ComboBox, Laborer3ComboBox };
        for (var slot = 1; slot <= 3; slot++)
        {
            var selected = combos[slot - 1].SelectedItem as string ?? "None";
            if (!selected.Equals("None", StringComparison.OrdinalIgnoreCase))
                dict[LaborerConfigHelper.LaborerKey(slot)] = IslandLaborerProfessions.GetToken(selected);
        }
        return dict.Count > 0 ? LaborerConfigHelper.BuildConfiguration(dict) : string.Empty;
    }

    private void PlotTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateConfigVisibility();
        UpdateInfoText();
    }

    private void QuantityTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateInfoText();
    }

    private void UpdateInfoText()
    {
        if (InfoText == null) return;
        var pt = GetSelectedPlotType();
        var hours = pt.GetBaseCollectionHours();
        var baseHint = hours > 0
            ? string.Format(LocalizationController.Translation("ISLAND_MANAGEMENT_PLOT_BASE_CYCLE_HINT"),
                pt.GetDisplayName(), hours.ToString("0.#"), pt.GetPremiumEffectSummary())
            : pt.GetDisplayName();

        var qty = int.TryParse(QuantityTextBox?.Text, out var q) ? q : 1;
        var shouldExpand = IslandPlotHelper.ShouldExpand(new IslandPlot(pt, qty));
        if (shouldExpand && qty > 1)
            InfoText.Text = string.Format(LocalizationController.Translation("ISLAND_MANAGEMENT_PLOT_QUANTITY_SPLIT_HINT"), baseHint, qty);
        else
            InfoText.Text = baseHint;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;

        var plotType = GetSelectedPlotType();
        var quantity = int.TryParse(QuantityTextBox.Text, out var q) ? Math.Max(1, q) : 1;
        var config = plotType == PlotType.House
            ? BuildHouseConfiguration()
            : plotType.HasFarmableConfig()
                ? BuildFarmableConfiguration(plotType)
                : ConfigurationTextBox.Text.Trim();

        if (_existingPlot != null)
        {
            _existingPlot.PlotType = plotType;
            _existingPlot.Quantity = quantity;
            _existingPlot.Configuration = config;
            _existingPlot.Notes = NotesTextBox.Text;
            Result = _existingPlot;
        }
        else
        {
            Result = new IslandPlot(plotType, quantity, NotesTextBox.Text, config);
        }

        DialogResult = true;
        Close();
    }

    private bool Validate()
    {
        if (PlotTypeComboBox.SelectedIndex < 0)
        {
            ShowError(LocalizationController.Translation("ISLAND_MANAGEMENT_VALIDATION_SELECT_PLOT_TYPE"));
            return false;
        }

        if (!int.TryParse(QuantityTextBox.Text, out var q) || q < 1)
        {
            ShowError(LocalizationController.Translation("ISLAND_MANAGEMENT_VALIDATION_QUANTITY_POSITIVE"));
            return false;
        }

        ValidationErrorPanel.Visibility = Visibility.Collapsed;
        return true;
    }

    private void ShowError(string message)
    {
        ValidationErrorText.Text = message;
        ValidationErrorPanel.Visibility = Visibility.Visible;
    }
}

public sealed class FarmableComboItem(string uniqueName, string displayName)
{
    public string UniqueName { get; } = uniqueName;
    public string DisplayName { get; } = displayName;
    public System.Windows.Media.Imaging.BitmapImage Icon =>
        UniqueName != null ? ImageController.GetItemImage(UniqueName, 24, 24) : null;
    public override string ToString() => DisplayName;
}
