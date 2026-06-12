using System.Globalization;

namespace TornStockTravel.Services;

public sealed record HistoryPredictionSample(
    DateTimeOffset ObservedAt,
    int Quantity,
    DateTimeOffset? RestockEstimateUtc,
    DateTimeOffset? StockoutEstimateUtc);

public sealed record PredictionSelloutEvent(
    TimeSpan Duration,
    DateTimeOffset StockStartedAtUtc,
    DateTimeOffset SoldOutAtUtc,
    int StartQuantity,
    DateTimeOffset? RestockEstimateUtc,
    DateTimeOffset? ApiStockoutEstimateUtc);

public sealed record ItemPrediction(
    int ItemId,
    string ItemName,
    string CountryCode,
    int SnapshotCount,
    int SelloutSampleCount,
    TimeSpan? MedianObservedAvailability,
    TimeSpan? FastestSellout,
    TimeSpan? SlowestSellout,
    DateTimeOffset? ApiStockoutUtc,
    DateTimeOffset? LocalStockoutUtc,
    DateTimeOffset? BlendedStockoutUtc,
    string Source,
    string Confidence,
    string Explanation)
{
    public bool HasLocalSignal => SelloutSampleCount > 0 && MedianObservedAvailability is not null;

    public DateTimeOffset? PredictedStockoutUtc => BlendedStockoutUtc ?? LocalStockoutUtc ?? ApiStockoutUtc;

    public TimeSpan? ApiLocalDelta => ApiStockoutUtc is null || LocalStockoutUtc is null
        ? null
        : (ApiStockoutUtc.Value - LocalStockoutUtc.Value).Duration();

    public string SourceText => $"Prediction: {Source}, {Confidence}";

    public string AvailabilityText => MedianObservedAvailability is null
        ? "Local median: not enough history"
        : $"Local median: {FormatDuration(MedianObservedAvailability.Value)}";

    public string RangeText => FastestSellout is null || SlowestSellout is null
        ? "Observed range: -"
        : $"Observed range: {FormatDuration(FastestSellout.Value)} - {FormatDuration(SlowestSellout.Value)}";

    public string StockoutText => PredictedStockoutUtc is null
        ? "Predicted stockout: -"
        : $"Predicted stockout: {PredictedStockoutUtc.Value.LocalDateTime.ToString("dd.MM. HH:mm", CultureInfo.CurrentCulture)}";

    public string LatestSafeArrivalText => PredictedStockoutUtc is null
        ? "Latest safe arrival: -"
        : $"Latest safe arrival: {PredictedStockoutUtc.Value.LocalDateTime.ToString("HH:mm", CultureInfo.CurrentCulture)}";

    public string ApiLocalDeltaText => ApiLocalDelta is null
        ? "API vs local: -"
        : $"API vs local: {FormatDuration(ApiLocalDelta.Value)} apart";

    public string SampleText => $"{SelloutSampleCount:N0} sellout samples from {SnapshotCount:N0} snapshots";

    public string PlannerText => $"Prediction: {Source}, {Confidence}";

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{Math.Max(1, duration.Minutes)}m";
    }
}
