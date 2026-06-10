using StatisticsAnalysisTool.Enumerations;
using StatisticsAnalysisTool.Localization;
using StatisticsAnalysisTool.Models.BindingModel;
using System;
using System.Windows;
using System.Windows.Input;

namespace StatisticsAnalysisTool.Views;

public partial class AddEditIslandWindow : Window
{
    private readonly IslandEntry _existingIsland;

    public IslandEntry Result { get; private set; }
    public bool DeleteRequested { get; private set; }

    public AddEditIslandWindow(IslandEntry existingIsland = null, bool isEdit = true)
    {
        InitializeComponent();

        _existingIsland = isEdit ? existingIsland : null;

        if (existingIsland != null && isEdit)
        {
            TitleText.Text = LocalizationController.Translation("ISLAND_MANAGEMENT_DIALOG_EDIT_ISLAND");
            TitleIcon.Icon = FontAwesome5.EFontAwesomeIcon.Solid_Edit;
            ConfirmText.Text = LocalizationController.Translation("ISLAND_MANAGEMENT_DIALOG_SAVE_CHANGES");
            ConfirmIcon.Icon = FontAwesome5.EFontAwesomeIcon.Solid_Save;
            DeleteButton.Visibility = Visibility.Visible;

            NameTextBox.Text = existingIsland.Name;
            TierComboBox.SelectedIndex = Math.Max(0, existingIsland.Tier - 1);
            CityComboBox.SelectedIndex = CityFactionToComboIndex(existingIsland.CityFaction);
            OwnerTextBox.Text = existingIsland.OwnerName;
            PlotCountTextBox.Text = existingIsland.PlotCount.ToString();
            PremiumCheckBox.IsChecked = existingIsland.HasPremium;
            TrackingCheckBox.IsChecked = existingIsland.TrackingEnabled;
            NotesTextBox.Text = existingIsland.Notes;
            VisitDurationTextBox.Text = existingIsland.VisitDurationMinutes.HasValue
                ? existingIsland.VisitDurationMinutes.Value.ToString()
                : string.Empty;
        }
        else if (existingIsland != null)
        {
            // Prefill-only: keep "Add Island" UI but populate known fields
            NameTextBox.Text = existingIsland.Name;
            TierComboBox.SelectedIndex = existingIsland.Tier > 0 ? existingIsland.Tier - 1 : 5;
            if (existingIsland.CityFaction != CityFaction.Unknown)
                CityComboBox.SelectedIndex = CityFactionToComboIndex(existingIsland.CityFaction);
            OwnerTextBox.Text = existingIsland.OwnerName;
            PremiumCheckBox.IsChecked = existingIsland.HasPremium;
        }
        else
        {
            TierComboBox.SelectedIndex = 5;
        }
    }

    private void CityComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // biome derived from city — no action needed in UI
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

        var tier = TierComboBox.SelectedIndex + 1;
        var city = ComboIndexToCityFaction(CityComboBox.SelectedIndex);
        var cityName = (CityComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        var biome = CityToBiome(city);

        int? visitDuration = int.TryParse(VisitDurationTextBox.Text.Trim(), out var vd) && vd >= 0 ? vd : (int?)null;

        if (_existingIsland != null)
        {
            _existingIsland.Name = NameTextBox.Text.Trim();
            _existingIsland.Tier = tier;
            _existingIsland.TierDisplay = $"T{tier}";
            _existingIsland.CityFaction = city;
            _existingIsland.CityName = cityName;
            _existingIsland.Biome = biome;
            _existingIsland.OwnerName = OwnerTextBox.Text.Trim();
            _existingIsland.PlotCount = int.TryParse(PlotCountTextBox.Text, out var pc) ? pc : 0;
            _existingIsland.HasPremium = PremiumCheckBox.IsChecked == true;
            _existingIsland.TrackingEnabled = TrackingCheckBox.IsChecked == true;
            _existingIsland.Notes = NotesTextBox.Text;
            _existingIsland.VisitDurationMinutes = visitDuration;
            Result = _existingIsland;
        }
        else
        {
            Result = new IslandEntry
            {
                Name = NameTextBox.Text.Trim(),
                Tier = tier,
                TierDisplay = $"T{tier}",
                CityFaction = city,
                CityName = cityName,
                Biome = biome,
                OwnerName = OwnerTextBox.Text.Trim(),
                PlotCount = int.TryParse(PlotCountTextBox.Text, out var pc) ? pc : 0,
                HasPremium = PremiumCheckBox.IsChecked == true,
                TrackingEnabled = TrackingCheckBox.IsChecked == true,
                Notes = NotesTextBox.Text,
                VisitDurationMinutes = visitDuration,
                CollectionStatusText = "Visit island to update",
                CollectionStatusState = "unknown",
                NeedsVisit = true,
                LastVisited = null
            };
        }

        DialogResult = true;
        Close();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            string.Format(LocalizationController.Translation("ISLAND_MANAGEMENT_DELETE_CONFIRM"), _existingIsland?.Name),
            LocalizationController.Translation("ISLAND_MANAGEMENT_DELETE_ISLAND"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        DeleteRequested = true;
        DialogResult = true;
        Close();
    }

    private bool Validate()
    {
        if (string.IsNullOrWhiteSpace(NameTextBox.Text))
        {
            ShowError(LocalizationController.Translation("ISLAND_MANAGEMENT_VALIDATION_NAME_REQUIRED"));
            return false;
        }
        if (CityComboBox.SelectedIndex < 0)
        {
            ShowError(LocalizationController.Translation("ISLAND_MANAGEMENT_VALIDATION_SELECT_CITY"));
            return false;
        }
        if (IsDuplicateName())
        {
            ShowError(LocalizationController.Translation("ISLAND_MANAGEMENT_VALIDATION_DUPLICATE_NAME"));
            return false;
        }
        ValidationErrorPanel.Visibility = Visibility.Collapsed;
        return true;
    }

    // Rejects a name+city that already belongs to another island. On edit the island being edited is
    // excluded by its id, so re-saving it (or editing unrelated fields) is not flagged as its own duplicate.
    private bool IsDuplicateName()
    {
        var controller = Common.ServiceLocator.Resolve<Network.Manager.TrackingController>()?.IslandController;
        if (controller == null) return false;

        var name = NameTextBox.Text.Trim();
        var cityName = (CityComboBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        return controller.IslandExists(name, cityName, _existingIsland?.IslandId);
    }

    private void ShowError(string message)
    {
        ValidationErrorText.Text = message;
        ValidationErrorPanel.Visibility = Visibility.Visible;
    }

    private static string CityToBiome(CityFaction faction) => faction switch
    {
        CityFaction.Bridgewatch  => "Steppe",
        CityFaction.Thetford     => "Swamp",
        CityFaction.Lymhurst     => "Forest",
        CityFaction.Brecilien    => "Forest",
        CityFaction.Martlock     => "Highland",
        CityFaction.FortSterling => "Mountain",
        CityFaction.Caerleon     => "Steppe",
        _                        => string.Empty
    };

    private static int CityFactionToComboIndex(CityFaction faction) => faction switch
    {
        CityFaction.Bridgewatch  => 0,
        CityFaction.Lymhurst    => 1,
        CityFaction.Martlock    => 2,
        CityFaction.FortSterling => 3,
        CityFaction.Thetford    => 4,
        CityFaction.Caerleon    => 5,
        CityFaction.Brecilien   => 6,
        _                       => 0
    };

    private static CityFaction ComboIndexToCityFaction(int index) => index switch
    {
        0 => CityFaction.Bridgewatch,
        1 => CityFaction.Lymhurst,
        2 => CityFaction.Martlock,
        3 => CityFaction.FortSterling,
        4 => CityFaction.Thetford,
        5 => CityFaction.Caerleon,
        6 => CityFaction.Brecilien,
        _ => CityFaction.Unknown
    };
}
