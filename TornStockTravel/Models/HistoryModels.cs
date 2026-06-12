using System.Globalization;

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

public sealed record HistoryOverview(
    int RefreshSnapshotCount,
    int ItemSnapshotCount,
    int MarketSnapshotCount,
    int RestockSnapshotCount,
    int LatestItemCount,
    DateTimeOffset? FirstObservedAt,
    DateTimeOffset? LastObservedAt,
    IReadOnlyList<HistoryItemOption> ItemOptions,
    IReadOnlyList<HistoryProfitOpportunity> TopProfitItems,
    IReadOnlyList<HistoryMarketMover> MarketMovers,
    IReadOnlyList<HistoryRestockOpportunity> UpcomingRestocks)
{
    public bool HasData => RefreshSnapshotCount > 0;
}

public sealed record HistoryItemOption(
    int ItemId,
    string ItemName,
    string CountryCode,
    string CountryName,
    int SnapshotCount,
    DateTimeOffset LastObservedAt)
{
    public string Key => $"{CountryCode.ToLowerInvariant()}:{ItemId}";
    public string DisplayText => $"{ItemName} - {CountryName} ({CountryCode.ToUpperInvariant()})";
    public string DetailsText => $"{SnapshotCount:N0} snapshots | latest {LastObservedAt.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture)}";

    public override string ToString()
    {
        return DisplayText;
    }
}

public sealed record HistoryProfitOpportunity(
    string ItemName,
    string CountryCode,
    string CountryName,
    decimal ProfitPerHour,
    decimal Profit,
    int Quantity,
    decimal? MarketValue,
    decimal? UnitProfit)
{
    public string DestinationText => $"{CountryName} ({CountryCode.ToUpperInvariant()})";
    public string ProfitPerHourText => $"${ProfitPerHour.ToString("N0", CultureInfo.CurrentCulture)}/h";
    public string ProfitText => $"${Profit.ToString("N0", CultureInfo.CurrentCulture)} profit";
    public string QuantityText => $"{Quantity:N0} stock";
    public string MarketValueText => MarketValue is null
        ? "Market: -"
        : $"Market: ${MarketValue.Value.ToString("N0", CultureInfo.CurrentCulture)}";
    public string UnitProfitText => UnitProfit is null
        ? "Unit profit: -"
        : $"Unit profit: ${UnitProfit.Value.ToString("N0", CultureInfo.CurrentCulture)}";
}

public sealed record HistoryItemDetail(
    int ItemId,
    string ItemName,
    string CountryCode,
    string CountryName,
    int SnapshotCount,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    int LatestQuantity,
    decimal LatestCost,
    decimal? LatestMarketValue,
    decimal? LatestEffectiveValue,
    decimal? LatestUnitProfit,
    decimal? LatestProfitPerHour,
    decimal? AverageProfitPerHour,
    decimal? MinMarketValue,
    decimal? MaxMarketValue,
    int ZeroStockSnapshots,
    DateTimeOffset? LatestRestockEstimateUtc,
    DateTimeOffset? LatestStockoutEstimateUtc,
    string? LatestRestockConfidence,
    string? LatestStockoutConfidence,
    IReadOnlyList<HistoryItemSample> RecentSamples)
{
    public string Title => $"{ItemName} - {CountryName} ({CountryCode.ToUpperInvariant()})";
    public string SnapshotText => $"{SnapshotCount:N0} snapshots from {FirstObservedAt.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture)} to {LastObservedAt.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture)}";
    public string LatestStockText => $"{LatestQuantity:N0}";
    public string LatestCostText => $"${LatestCost.ToString("N0", CultureInfo.CurrentCulture)}";
    public string LatestMarketText => FormatMoney(LatestMarketValue);
    public string LatestEffectiveText => FormatMoney(LatestEffectiveValue);
    public string LatestUnitProfitText => FormatMoney(LatestUnitProfit);
    public string LatestProfitPerHourText => FormatMoneyPerHour(LatestProfitPerHour);
    public string AverageProfitPerHourText => FormatMoneyPerHour(AverageProfitPerHour);
    public string MarketRangeText => MinMarketValue is null || MaxMarketValue is null
        ? "-"
        : $"{FormatMoney(MinMarketValue)} - {FormatMoney(MaxMarketValue)}";
    public string ZeroStockText => $"{ZeroStockSnapshots:N0} zero-stock snapshots";
    public string LatestRestockText => LatestRestockEstimateUtc is null
        ? "-"
        : LatestRestockEstimateUtc.Value.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture);
    public string LatestStockoutText => LatestStockoutEstimateUtc is null
        ? "-"
        : LatestStockoutEstimateUtc.Value.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture);
    public string LatestConfidenceText => $"Restock: {LatestRestockConfidence ?? "-"} | Stockout: {LatestStockoutConfidence ?? "-"}";

    private static string FormatMoney(decimal? value)
    {
        return value is null
            ? "-"
            : $"${value.Value.ToString("N0", CultureInfo.CurrentCulture)}";
    }

    private static string FormatMoneyPerHour(decimal? value)
    {
        return value is null
            ? "-"
            : $"${value.Value.ToString("N0", CultureInfo.CurrentCulture)}/h";
    }
}

public sealed record HistoryItemSample(
    DateTimeOffset ObservedAt,
    int Quantity,
    decimal Cost,
    decimal? MarketValue,
    decimal? EffectiveValue,
    decimal? UnitProfit,
    decimal? ProfitPerHour,
    DateTimeOffset? RestockEstimateUtc,
    DateTimeOffset? StockoutEstimateUtc)
{
    public string ObservedText => ObservedAt.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture);
    public string QuantityText => Quantity.ToString("N0", CultureInfo.CurrentCulture);
    public string CostText => $"${Cost.ToString("N0", CultureInfo.CurrentCulture)}";
    public string MarketValueText => FormatMoney(MarketValue);
    public string EffectiveValueText => FormatMoney(EffectiveValue);
    public string UnitProfitText => FormatMoney(UnitProfit);
    public string ProfitPerHourText => ProfitPerHour is null
        ? "-"
        : $"${ProfitPerHour.Value.ToString("N0", CultureInfo.CurrentCulture)}/h";
    public string RestockText => RestockEstimateUtc is null
        ? "-"
        : RestockEstimateUtc.Value.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture);
    public string StockoutText => StockoutEstimateUtc is null
        ? "-"
        : StockoutEstimateUtc.Value.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture);

    private static string FormatMoney(decimal? value)
    {
        return value is null
            ? "-"
            : $"${value.Value.ToString("N0", CultureInfo.CurrentCulture)}";
    }
}

public sealed record HistoryMarketMover(
    int ItemId,
    string ItemName,
    decimal FirstMarketValue,
    decimal LatestMarketValue,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LatestObservedAt)
{
    public decimal AbsoluteChange => LatestMarketValue - FirstMarketValue;
    public decimal PercentChange => FirstMarketValue <= 0 ? 0 : AbsoluteChange / FirstMarketValue * 100;
    public string DirectionText => AbsoluteChange switch
    {
        > 0 => "Up",
        < 0 => "Down",
        _ => "Flat"
    };
    public string ChangeText => $"{DirectionText} ${Math.Abs(AbsoluteChange).ToString("N0", CultureInfo.CurrentCulture)} ({PercentChange:+0.0;-0.0;0.0}%)";
    public string ValueText => $"${FirstMarketValue.ToString("N0", CultureInfo.CurrentCulture)} -> ${LatestMarketValue.ToString("N0", CultureInfo.CurrentCulture)}";
}

public sealed record HistoryRestockOpportunity(
    string ItemName,
    string CountryCode,
    string CountryName,
    DateTimeOffset RestockEstimateUtc,
    DateTimeOffset? StockoutEstimateUtc,
    string? RestockConfidence,
    string? StockoutConfidence,
    TimeSpan FlightDuration,
    DateTimeOffset SuggestedDepartureUtc,
    DateTimeOffset LatestDepartureUtc,
    DateTimeOffset ExpectedArrivalUtc)
{
    public string DestinationText => $"{CountryName} ({CountryCode.ToUpperInvariant()})";
    public string RestockTimeText => RestockEstimateUtc.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture);
    public string StockoutTimeText => StockoutEstimateUtc is null
        ? "Stockout: -"
        : $"Stockout: {StockoutEstimateUtc.Value.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture)}";
    public string SuggestedDepartureText => $"Depart: {SuggestedDepartureUtc.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture)}";
    public string LatestDepartureText => $"Latest: {LatestDepartureUtc.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture)}";
    public string ExpectedArrivalText => $"Arrive: {ExpectedArrivalUtc.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture)}";
    public string FlightDurationText => FlightDuration.TotalHours >= 1
        ? $"{(int)FlightDuration.TotalHours}h {FlightDuration.Minutes}m flight"
        : $"{FlightDuration.Minutes}m flight";
    public string ReachabilityText => $"{SuggestedDepartureText} | {LatestDepartureText} | {ExpectedArrivalText}";
    public string ConfidenceText => $"Restock: {RestockConfidence ?? "-"} | Stockout: {StockoutConfidence ?? "-"}";
}
