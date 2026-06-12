using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TornStockTravel.Services;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace TornStockTravel.Views;

public partial class StockOverviewView : System.Windows.Controls.UserControl
{
    private IReadOnlyList<TravelDestination> _allDestinations = Array.Empty<TravelDestination>();

    public StockOverviewView()
    {
        InitializeComponent();
    }

    public Func<int, string, Task<string?>>? BazaarPriceCommitRequested { get; set; }

    public int VisibleItemCount { get; private set; }

    public void SetDestinations(IReadOnlyList<TravelDestination> destinations)
    {
        _allDestinations = destinations;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void CountryFilter_Changed(object sender, RoutedEventArgs e)
    {
        ApplyFilter();
    }

    private async void BazaarPriceBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is WpfTextBox textBox)
        {
            await CommitBazaarPriceAsync(textBox);
        }
    }

    private async void BazaarPriceBox_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not WpfTextBox textBox)
        {
            return;
        }

        e.Handled = true;
        await CommitBazaarPriceAsync(textBox);
        Keyboard.ClearFocus();
    }

    private async Task CommitBazaarPriceAsync(WpfTextBox textBox)
    {
        if (BazaarPriceCommitRequested is null || textBox.Tag is not int itemId || itemId <= 0)
        {
            return;
        }

        string? replacementText = await BazaarPriceCommitRequested.Invoke(itemId, textBox.Text.Trim());
        if (replacementText is not null)
        {
            textBox.Text = replacementText;
        }
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        HashSet<string> selectedCountryCodes = GetSelectedCountryCodes();
        IReadOnlyList<TravelDestination> visibleDestinations = _allDestinations;

        if (selectedCountryCodes.Count > 0)
        {
            visibleDestinations = visibleDestinations
                .Where(destination => selectedCountryCodes.Contains(destination.Code))
                .ToList();
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            visibleDestinations = visibleDestinations
                .Select(destination =>
                {
                    bool destinationMatches =
                        destination.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || destination.Code.Contains(query, StringComparison.CurrentCultureIgnoreCase);

                    IReadOnlyList<TravelItem> items = destinationMatches
                        ? destination.Items
                        : destination.Items
                            .Where(item => item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                            .ToList();

                    return destination with
                    {
                        Items = items
                    };
                })
                .Where(destination => destination.ItemCount > 0)
                .ToList();
        }

        DestinationList.ItemsSource = visibleDestinations;
        VisibleItemCount = visibleDestinations.Sum(destination => destination.ItemCount);
        EmptyDashboardText.Visibility = visibleDestinations.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private HashSet<string> GetSelectedCountryCodes()
    {
        return CountryFilterPanel.Children
            .OfType<WpfCheckBox>()
            .Where(checkBox => checkBox.IsChecked == true && checkBox.Tag is string)
            .Select(checkBox => ((string)checkBox.Tag).ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
