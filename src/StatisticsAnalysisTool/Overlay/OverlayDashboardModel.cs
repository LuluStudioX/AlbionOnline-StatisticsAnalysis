using System;
using System.Windows;
using StatisticsAnalysisTool.Models.BindingModel;
using StatisticsAnalysisTool.ViewModels;

namespace StatisticsAnalysisTool.Overlay
{
    /// <summary>
    /// A lightweight bindings model intended solely for overlay/streaming UI.
    /// This keeps overlay concerns separated from the main DashboardBindings model.
    /// </summary>
    public class OverlayDashboardModel : BaseViewModel
    {
        public double FamePerHour { get; set; }
        public double SilverPerHour { get; set; }
        public double ReSpecPointsPerHour { get; set; }
        public double MightPerHour { get; set; }
        public double FavorPerHour { get; set; }

        public double TotalGainedFameInSession { get; set; }
        public double TotalGainedSilverInSession { get; set; }
        public double TotalGainedReSpecPointsInSession { get; set; }
        public double TotalGainedMightInSession { get; set; }
        public double TotalGainedFavorInSession { get; set; }

        public double FameInPercent { get; set; }
        public double SilverInPercent { get; set; }
        public double ReSpecPointsInPercent { get; set; }
        public double MightInPercent { get; set; }
        public double FavorInPercent { get; set; }

        public double HighestValue { get; set; }

        public Visibility FamePerHourVisibility { get; set; } = Visibility.Visible;
        public Visibility SilverPerHourVisibility { get; set; } = Visibility.Visible;
        public Visibility ReSpecPerHourVisibility { get; set; } = Visibility.Visible;
        public Visibility MightPerHourVisibility { get; set; } = Visibility.Visible;
        public Visibility FavorPerHourVisibility { get; set; } = Visibility.Visible;

        /// <summary>
        /// Copies overlay-relevant properties from the canonical DashboardBindings model.
        /// This method is intentionally explicit to avoid coupling and accidental UI calls.
        /// </summary>
        public void CopyFrom(DashboardBindings dashboard)
        {
            if (dashboard == null) throw new ArgumentNullException(nameof(dashboard));

            FamePerHour = dashboard.FamePerHour;
            SilverPerHour = dashboard.SilverPerHour;
            ReSpecPointsPerHour = dashboard.ReSpecPointsPerHour;
            MightPerHour = dashboard.MightPerHour;
            FavorPerHour = dashboard.FavorPerHour;

            TotalGainedFameInSession = dashboard.TotalGainedFameInSession;
            TotalGainedSilverInSession = dashboard.TotalGainedSilverInSession;
            TotalGainedReSpecPointsInSession = dashboard.TotalGainedReSpecPointsInSession;
            TotalGainedMightInSession = dashboard.TotalGainedMightInSession;
            TotalGainedFavorInSession = dashboard.TotalGainedFavorInSession;

            FameInPercent = dashboard.FameInPercent;
            SilverInPercent = dashboard.SilverInPercent;
            ReSpecPointsInPercent = dashboard.ReSpecPointsInPercent;
            MightInPercent = dashboard.MightInPercent;
            FavorInPercent = dashboard.FavorInPercent;

            HighestValue = dashboard.HighestValue;

            // Copy visibility settings if the overlay wants to respect them
            FamePerHourVisibility = Visibility.Visible;
            SilverPerHourVisibility = Visibility.Visible;
            ReSpecPerHourVisibility = Visibility.Visible;
            MightPerHourVisibility = Visibility.Visible;
            FavorPerHourVisibility = Visibility.Visible;

            // Notify bindings
            OnPropertyChanged(string.Empty);
        }
    }
}
