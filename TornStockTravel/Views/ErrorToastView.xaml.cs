using System.Windows;

namespace TornStockTravel.Views;

public partial class ErrorToastView : System.Windows.Controls.UserControl
{
    public ErrorToastView()
    {
        InitializeComponent();
    }

    public event EventHandler? DismissRequested;

    public void ShowMessage(string message)
    {
        ErrorToastText.Text = message;
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }
}
