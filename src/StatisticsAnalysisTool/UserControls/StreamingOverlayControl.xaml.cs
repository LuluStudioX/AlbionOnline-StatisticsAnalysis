using System.Windows.Controls;

namespace StatisticsAnalysisTool.UserControls
{
    public partial class StreamingOverlayControl : UserControl
    {
        public StreamingOverlayControl()
        {
            InitializeComponent();
            this.DataContext = new StatisticsAnalysisTool.ViewModels.StreamingOverlayViewModel();
        }
    }
}
