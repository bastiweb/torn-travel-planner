namespace TornStockTravel.Services;

public sealed record InventoryValueSummary(
    decimal TotalValue,
    decimal ExpectedProfit,
    int RelevantOwnedItems,
    int WatchlistMatches)
{
    public string TotalValueText => TotalValue.ToString("N0");

    public string ExpectedProfitText => ExpectedProfit.ToString("N0");

    public string RelevantOwnedItemsText => RelevantOwnedItems.ToString("N0");

    public string WatchlistMatchesText => WatchlistMatches.ToString("N0");
}

public sealed record CountrySummaryCard(
    string CountryName,
    string CountryCode,
    int ProfitableItemCount,
    decimal BestProfitPerHour,
    string BestItemName,
    DateTimeOffset? NextRestockAt)
{
    public string Title => $"{CountryName} ({CountryCode.ToUpperInvariant()})";

    public string ProfitableItemCountText => ProfitableItemCount.ToString("N0");

    public string BestProfitPerHourText => BestProfitPerHour <= 0 ? "-" : $"${BestProfitPerHour:N0}/h";

    public string BestItemText => string.IsNullOrWhiteSpace(BestItemName) ? "-" : BestItemName;

    public string NextRestockText => NextRestockAt is null
        ? "-"
        : NextRestockAt.Value.LocalDateTime.ToString("HH:mm");

    public string HeatBrush => BestProfitPerHour switch
    {
        >= 250_000 => "#14532D",
        >= 100_000 => "#164E63",
        >= 50_000 => "#1E3A8A",
        > 0 => "#3F2D12",
        _ => "#111827"
    };
}

public sealed record WatchlistItemView(
    string ItemName,
    string DestinationText,
    string ProfitPerHourText,
    string StockText);
