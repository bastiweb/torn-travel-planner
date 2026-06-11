namespace TornStockTravel.Services;

public sealed record HistoryTrendInfo(
    int SnapshotCount,
    string ProfitTrend,
    string StockTrend,
    string MarketTrend,
    string SelloutBehavior)
{
    public bool HasEnoughData => SnapshotCount >= 3;

    public string SummaryText => HasEnoughData
        ? $"{ProfitTrend} profit | {SelloutBehavior}"
        : "History: collecting data";

    public string DetailsText => HasEnoughData
        ? $"{SnapshotCount:N0} snapshots. Stock: {StockTrend}. Market: {MarketTrend}."
        : $"{SnapshotCount:N0} snapshots collected so far.";
}

public sealed record HistorySample(
    DateTimeOffset ObservedAt,
    int Quantity,
    decimal? MarketValue,
    decimal? UnitProfit,
    decimal? ProfitPerHour);
