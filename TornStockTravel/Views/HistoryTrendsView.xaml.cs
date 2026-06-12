using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TornStockTravel.Services;

namespace TornStockTravel.Views;

public partial class HistoryTrendsView : System.Windows.Controls.UserControl
{
    private bool _isApplyingOverview;

    public HistoryTrendsView()
    {
        InitializeComponent();
    }

    public event EventHandler<HistoryItemSelectionChangedEventArgs>? ItemSelectionChanged;

    public HistoryItemOption? SelectedHistoryItem => HistoryItemBox.SelectedItem as HistoryItemOption;

    public void SetOverview(HistoryOverview overview)
    {
        string? selectedKey = SelectedHistoryItem?.Key;

        HistoryStatusText.Text = overview.HasData ? "Collecting insights" : "Waiting for history";
        HistoryWindowText.Text = overview.HasData
            ? $"From {FormatDate(overview.FirstObservedAt)} to {FormatDate(overview.LastObservedAt)}"
            : "Refresh data once to start the local history.";
        RefreshSnapshotCountText.Text = overview.RefreshSnapshotCount.ToString("N0", CultureInfo.CurrentCulture);
        LatestItemCountText.Text = $"{overview.LatestItemCount:N0} items in latest snapshot";
        ItemSnapshotCountText.Text = overview.ItemSnapshotCount.ToString("N0", CultureInfo.CurrentCulture);
        MarketSnapshotCountText.Text = $"{overview.MarketSnapshotCount:N0} market samples";
        RestockSnapshotCountText.Text = overview.RestockSnapshotCount.ToString("N0", CultureInfo.CurrentCulture);

        _isApplyingOverview = true;
        HistoryItemBox.ItemsSource = overview.ItemOptions;
        HistoryItemBox.SelectedItem = overview.ItemOptions.FirstOrDefault(option => option.Key == selectedKey)
            ?? overview.ItemOptions.FirstOrDefault();
        _isApplyingOverview = false;
        UpdateSelectedItemMeta();

        TopProfitItemsList.ItemsSource = overview.TopProfitItems;
        TopProfitEmptyText.Visibility = overview.TopProfitItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        MarketMoversList.ItemsSource = overview.MarketMovers;
        MarketMoversEmptyText.Visibility = overview.MarketMovers.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpcomingRestocksList.ItemsSource = overview.UpcomingRestocks;
        UpcomingRestocksEmptyText.Visibility = overview.UpcomingRestocks.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        FastSelloutItemsList.ItemsSource = overview.FastSelloutItems;
        FastSelloutEmptyText.Visibility = overview.FastSelloutItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        PredictionDisagreementsList.ItemsSource = overview.PredictionDisagreements;
        PredictionDisagreementsEmptyText.Visibility = overview.PredictionDisagreements.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public void SetItemDetail(HistoryItemDetail? detail)
    {
        if (detail is null)
        {
            ItemDetailPanel.Visibility = Visibility.Collapsed;
            ItemDetailEmptyText.Visibility = Visibility.Visible;
            return;
        }

        ItemDetailEmptyText.Visibility = Visibility.Collapsed;
        ItemDetailPanel.Visibility = Visibility.Visible;
        ItemDetailTitleText.Text = detail.Title;
        ItemDetailSnapshotText.Text = detail.SnapshotText;
        ItemLatestStockText.Text = detail.LatestStockText;
        ItemZeroStockText.Text = detail.ZeroStockText;
        ItemLatestMarketText.Text = detail.LatestMarketText;
        ItemMarketRangeText.Text = $"Range: {detail.MarketRangeText}";
        ItemLatestProfitPerHourText.Text = detail.LatestProfitPerHourText;
        ItemAverageProfitPerHourText.Text = $"Average: {detail.AverageProfitPerHourText}";
        ItemLatestRestockText.Text = $"Restock: {detail.LatestRestockText}";
        ItemLatestStockoutText.Text = $"Stockout: {detail.LatestStockoutText}";
        ItemLatestCostRun.Text = detail.LatestCostText;
        ItemLatestEffectiveRun.Text = detail.LatestEffectiveText;
        ItemLatestUnitProfitRun.Text = detail.LatestUnitProfitText;
        ItemLatestConfidenceText.Text = detail.LatestConfidenceText;
        ItemPredictionSourceText.Text = detail.PredictionSourceText;
        ItemPredictionAvailabilityText.Text = detail.PredictionAvailabilityText;
        ItemPredictionStockoutText.Text = detail.PredictionStockoutText;
        ItemPredictionDeltaText.Text = detail.PredictionDeltaText;
        ItemPredictionSamplesText.Text = detail.PredictionSampleText;
        ItemPredictionRangeText.Text = detail.PredictionRangeText;
        ItemPredictionExplanationText.Text = detail.PredictionExplanationText;
    }

    private void HistoryItemBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingOverview)
        {
            return;
        }

        UpdateSelectedItemMeta();
        ItemSelectionChanged?.Invoke(this, new HistoryItemSelectionChangedEventArgs(SelectedHistoryItem));
    }

    private void UpdateSelectedItemMeta()
    {
        SelectedHistoryItemMetaText.Text = SelectedHistoryItem?.DetailsText ?? "No historical items found yet.";
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value is null
            ? "-"
            : value.Value.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
    }
}

public sealed class HistoryItemSelectionChangedEventArgs : EventArgs
{
    public HistoryItemSelectionChangedEventArgs(HistoryItemOption? selectedItem)
    {
        SelectedItem = selectedItem;
    }

    public HistoryItemOption? SelectedItem { get; }
}
