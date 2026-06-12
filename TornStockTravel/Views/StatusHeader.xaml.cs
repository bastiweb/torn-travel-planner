using System.Windows;

namespace TornStockTravel.Views;

public partial class StatusHeader : System.Windows.Controls.UserControl
{
    public StatusHeader()
    {
        InitializeComponent();
    }

    public event EventHandler? RefreshRequested;

    public bool IsRefreshEnabled
    {
        get => RefreshButton.IsEnabled;
        set => RefreshButton.IsEnabled = value;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }
}
