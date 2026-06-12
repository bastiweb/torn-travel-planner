using System.Globalization;

namespace TornStockTravel.Services;

public sealed record TravelItem(
    int Id,
    string Name,
    int Quantity,
    decimal Cost,
    decimal? MarketValue,
    decimal? BazaarPrice,
    string? ImageUrl,
    int OwnedAmount,
    TimeSpan? FlightDuration,
    int BuyCapacity,
    RestockAvailabilityMode AvailabilityMode,
    DroqsRestockInfo? RestockInfo,
    HistoryTrendInfo? HistoryTrend = null,
    ItemPrediction? Prediction = null)
{
    public decimal? EffectiveValue => BazaarPrice ?? MarketValue;

    public decimal? UnitProfit => EffectiveValue is null ? null : EffectiveValue.Value - Cost;

    public bool HasRestockEstimate => RestockInfo is not null;

    public int BuyAmount => Quantity > 0
        ? BuyCapacity > 0 ? Math.Min(Quantity, BuyCapacity) : Quantity
        : HasRestockEstimate ? Math.Max(BuyCapacity, 1) : 0;

    public decimal? Profit => UnitProfit is null ? null : UnitProfit.Value * BuyAmount;

    public decimal CashNeeded => Cost * BuyAmount;

    public TimeSpan? RoundTripDuration => FlightDuration is null ? null : FlightDuration.Value * 2;

    public DateTimeOffset? RestockEstimateUtc => RestockInfo?.EstimatedAtUtc ?? ParseTornCityTime(RestockInfo?.EstimatedAtTct);

    public DateTimeOffset? StockoutEstimateUtc => RestockInfo?.StockoutAtUtc ?? ParseTornCityTime(RestockInfo?.StockoutAtTct);

    public DateTimeOffset? EffectiveStockoutEstimateUtc => Prediction?.PredictedStockoutUtc ?? StockoutEstimateUtc;

    public TimeSpan RestockArrivalLeadTime => TimeSpan.FromMinutes(2);

    public TimeSpan? RestockAvailabilityDuration => RestockInfo?.GetAvailabilityDuration(AvailabilityMode);

    public DateTimeOffset? RestockWindowStartUtc => RestockEstimateUtc?.Subtract(RestockArrivalLeadTime);

    public DateTimeOffset? RestockWindowEndUtc => RestockEstimateUtc is null
        ? null
        : RestockEstimateUtc.Value.Add(RestockAvailabilityDuration ?? RestockArrivalLeadTime);

    public DateTimeOffset? LatestDepartureUtc => RestockEstimateUtc is null || FlightDuration is null
        ? null
        : RestockWindowEndUtc?.Subtract(FlightDuration.Value);

    public bool IsDepartureTooLate => LatestDepartureUtc is not null
        && LatestDepartureUtc.Value < DateTimeOffset.UtcNow.AddMinutes(-30);

    public decimal? ProfitPerHour => Profit is null || RoundTripDuration is null || RoundTripDuration.Value.TotalHours <= 0
        ? null
        : Profit.Value / (decimal)RoundTripDuration.Value.TotalHours;

    public decimal? OwnedValue => EffectiveValue is null ? null : EffectiveValue.Value * OwnedAmount;

    public string MarketValueText => MarketValue is null ? "-" : MarketValue.Value.ToString("N0");

    public string BazaarPriceText => BazaarPrice is null ? string.Empty : BazaarPrice.Value.ToString("N0");

    public string EffectiveValueText => EffectiveValue is null ? "-" : EffectiveValue.Value.ToString("N0");

    public string UnitProfitText => UnitProfit is null ? "-" : UnitProfit.Value.ToString("N0");

    public string BuyAmountText => BuyAmount.ToString("N0");

    public string CashNeededText => CashNeeded.ToString("N0");

    public string ProfitText => Profit is null ? "-" : Profit.Value.ToString("N0");

    public string ProfitPerHourText => ProfitPerHour is null ? "-" : ProfitPerHour.Value.ToString("N0");

    public string OwnedValueText => OwnedValue is null ? "-" : OwnedValue.Value.ToString("N0");

    public string TrendSummaryText => HistoryTrend?.SummaryText ?? "History: collecting data";

    public string TrendDetailsText => HistoryTrend?.DetailsText ?? "No historical snapshots for this item yet.";

    public string PredictionSummaryText => Prediction?.SourceText ?? "Prediction: collecting data";

    public string PredictionDetailsText => Prediction is null
        ? "No local prediction yet."
        : $"{Prediction.AvailabilityText}. {Prediction.StockoutText}. {Prediction.SampleText}. {Prediction.ApiLocalDeltaText}.";

    public string RestockConfidenceText => RestockInfo?.Confidence ?? "-";

    public string RestockEstimateText => FormatLocalTime(RestockEstimateUtc);

    public string RestockEstimateLabelText => $"Restock ETA: {RestockEstimateText}";

    public string RestockWindowText => RestockEstimateUtc is null
        ? "-"
        : $"{FormatLocalTimeOnly(RestockWindowStartUtc)}-{FormatLocalTimeOnly(RestockWindowEndUtc)} local";

    public string RestockWindowLabelText => $"Arrival window: {RestockWindowText}";

    public string RestockAvailabilityText => RestockInfo?.GetAvailabilityLabel(AvailabilityMode) ?? "-";

    public string StockoutEstimateText => EffectiveStockoutEstimateUtc is null
        ? "-"
        : FormatLocalTime(EffectiveStockoutEstimateUtc);

    public string StockoutEstimateLabelText => $"Stockout ETA: {StockoutEstimateText}";

    public string StockoutConfidenceText => RestockInfo?.StockoutConfidenceText ?? "-";

    public string RestockConfidenceLabelText => $"Restock: {RestockConfidenceText}";

    public string StockoutConfidenceLabelText => $"Stockout: {StockoutConfidenceText}";

    public string LatestDepartureLocalText => LatestDepartureUtc is null
        ? "-"
        : IsDepartureTooLate ? "Too late"
        : LatestDepartureUtc.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public string FlightDurationText => RoundTripDuration is null
        ? "-"
        : FormatFlightDuration(RoundTripDuration.Value);

    private static string FormatFlightDuration(TimeSpan duration)
    {
        return duration.Hours > 0
            ? $"{duration.Hours}h {duration.Minutes}m"
            : $"{duration.Minutes}m";
    }

    private static string FormatLocalTime(DateTimeOffset? value)
    {
        return value is null
            ? "-"
            : value.Value.ToLocalTime().ToString("HH:mm 'local'", CultureInfo.InvariantCulture);
    }

    private static string FormatLocalTimeOnly(DateTimeOffset? value)
    {
        return value is null
            ? "-"
            : value.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ParseTornCityTime(string? tctTime)
    {
        if (string.IsNullOrWhiteSpace(tctTime))
        {
            return null;
        }

        var timePart = tctTime.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (timePart is null ||
            !TimeSpan.TryParseExact(timePart, new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var timeOfDay))
        {
            return null;
        }

        var utcNow = DateTimeOffset.UtcNow;
        var estimate = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, timeOfDay.Hours, timeOfDay.Minutes, 0, TimeSpan.Zero);

        if (estimate < utcNow.AddHours(-12))
        {
            estimate = estimate.AddDays(1);
        }

        return estimate;
    }
}

public sealed record TravelDestination(
    string Code,
    string Name,
    IReadOnlyList<TravelItem> Items)
{
    public string DisplayName => $"{Name} ({Code.ToUpperInvariant()})";

    public int ItemCount => Items.Count;

    public int TotalQuantity => Items.Sum(item => item.Quantity);

    public decimal TotalCost => Items.Sum(item => item.Cost);
}
