using System.Windows;
using System.Windows.Input;

namespace StatisticsAnalysisTool.Views;

public partial class ImportCodeWindow : Window
{
    public string EnteredCode { get; private set; }

    public ImportCodeWindow()
    {
        InitializeComponent();
    }

    private void CodeTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var text = CodeTextBox.Text.Trim();
        ConfirmButton.IsEnabled = text.StartsWith("SAT:", System.StringComparison.OrdinalIgnoreCase) && text.Length > 4;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        EnteredCode = CodeTextBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }
}
