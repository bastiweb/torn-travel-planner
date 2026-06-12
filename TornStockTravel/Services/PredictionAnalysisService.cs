namespace TornStockTravel.Services;

public static class PredictionAnalysisService
{
    public static ItemPrediction Analyze(
        int itemId,
        string itemName,
        string countryCode,
        int snapshotCount,
        IReadOnlyList<PredictionSelloutEvent> selloutEvents,
        DateTimeOffset? currentStockStartedAt,
        DateTimeOffset? apiRestockAt,
        DateTimeOffset? apiStockoutAt,
        DateTimeOffset now)
    {
        List<TimeSpan> durations = selloutEvents
            .Select(selloutEvent => selloutEvent.Duration)
            .OrderBy(duration => duration)
            .ToList();
        TimeSpan? medianAvailability = durations.Count == 0 ? null : Median(durations);
        TimeSpan? fastestSellout = durations.Count == 0 ? null : durations.First();
        TimeSpan? slowestSellout = durations.Count == 0 ? null : durations.Last();
        DateTimeOffset? localStockoutAt = BuildLocalStockoutEstimate(currentStockStartedAt, apiRestockAt, medianAvailability, now);
        DateTimeOffset? blendedStockoutAt = BlendStockoutEstimate(localStockoutAt, apiStockoutAt, durations.Count);
        string source = BuildSource(durations.Count, localStockoutAt, apiStockoutAt);
        string confidence = BuildConfidence(durations.Count, apiStockoutAt);

        return new ItemPrediction(
            itemId,
            itemName,
            countryCode,
            snapshotCount,
            durations.Count,
            medianAvailability,
            fastestSellout,
            slowestSellout,
            apiStockoutAt,
            localStockoutAt,
            blendedStockoutAt,
            source,
            confidence,
            BuildExplanation(source, durations.Count, medianAvailability, apiStockoutAt, localStockoutAt));
    }

    public static IReadOnlyList<TravelDestination> ApplyPredictions(
        IReadOnlyList<TravelDestination> destinations,
        IReadOnlyDictionary<string, ItemPrediction> predictions)
    {
        return destinations
            .Select(destination => destination with
            {
                Items = destination.Items
                    .Select(item =>
                    {
                        string key = HistoryDatabaseService.BuildTrendKey(item.Id, destination.Code);
                        return predictions.TryGetValue(key, out ItemPrediction? prediction)
                            ? item with { Prediction = prediction }
                            : item;
                    })
                    .ToList()
            })
            .ToList();
    }

    private static DateTimeOffset? BuildLocalStockoutEstimate(
        DateTimeOffset? currentStockStartedAt,
        DateTimeOffset? apiRestockAt,
        TimeSpan? medianAvailability,
        DateTimeOffset now)
    {
        if (medianAvailability is null)
        {
            return null;
        }

        if (currentStockStartedAt is not null)
        {
            DateTimeOffset estimate = currentStockStartedAt.Value.Add(medianAvailability.Value);
            return estimate > now ? estimate : now.AddMinutes(1);
        }

        return apiRestockAt?.Add(medianAvailability.Value);
    }

    private static DateTimeOffset? BlendStockoutEstimate(
        DateTimeOffset? localStockoutAt,
        DateTimeOffset? apiStockoutAt,
        int selloutSampleCount)
    {
        if (localStockoutAt is null)
        {
            return apiStockoutAt;
        }

        if (apiStockoutAt is null)
        {
            return localStockoutAt;
        }

        double localWeight = selloutSampleCount switch
        {
            >= 8 => 0.70,
            >= 3 => 0.55,
            _ => 0.35
        };
        double apiWeight = 1.0 - localWeight;
        long blendedTicks = (long)((localStockoutAt.Value.UtcTicks * localWeight) + (apiStockoutAt.Value.UtcTicks * apiWeight));
        return new DateTimeOffset(blendedTicks, TimeSpan.Zero);
    }

    private static TimeSpan Median(IReadOnlyList<TimeSpan> values)
    {
        int middle = values.Count / 2;

        return values.Count % 2 == 1
            ? values[middle]
            : TimeSpan.FromTicks((values[middle - 1].Ticks + values[middle].Ticks) / 2);
    }

    private static string BuildSource(
        int selloutSampleCount,
        DateTimeOffset? localStockoutAt,
        DateTimeOffset? apiStockoutAt)
    {
        if (selloutSampleCount == 0)
        {
            return apiStockoutAt is null ? "collecting" : "API only";
        }

        if (localStockoutAt is not null && apiStockoutAt is not null)
        {
            return selloutSampleCount >= 8 ? "strong local blend" : "blended";
        }

        return selloutSampleCount >= 8 ? "strong local" : "local";
    }

    private static string BuildConfidence(int selloutSampleCount, DateTimeOffset? apiStockoutAt)
    {
        if (selloutSampleCount >= 8)
        {
            return "strong local";
        }

        if (selloutSampleCount >= 3 && apiStockoutAt is not null)
        {
            return "medium";
        }

        if (selloutSampleCount >= 3)
        {
            return "local";
        }

        if (selloutSampleCount >= 1)
        {
            return "low local";
        }

        return apiStockoutAt is null ? "collecting" : "API only";
    }

    private static string BuildExplanation(
        string source,
        int selloutSampleCount,
        TimeSpan? medianAvailability,
        DateTimeOffset? apiStockoutAt,
        DateTimeOffset? localStockoutAt)
    {
        if (medianAvailability is null)
        {
            return apiStockoutAt is null
                ? "Waiting for observed sellout history."
                : "Using API stockout estimate until local sellout history is available.";
        }

        string median = ItemPrediction.FormatDuration(medianAvailability.Value);
        if (apiStockoutAt is not null && localStockoutAt is not null)
        {
            return $"Blends API stockout with {selloutSampleCount:N0} local sellout samples; local median availability is {median}.";
        }

        return source.Contains("local", StringComparison.OrdinalIgnoreCase)
            ? $"Uses {selloutSampleCount:N0} local sellout samples; local median availability is {median}."
            : $"Local median availability is {median}, but more samples are needed.";
    }
}
