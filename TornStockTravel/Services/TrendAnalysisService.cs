namespace TornStockTravel.Services;

public static class TrendAnalysisService
{
    public static HistoryTrendInfo Analyze(IReadOnlyList<HistorySample> samples)
    {
        if (samples.Count == 0)
        {
            return new HistoryTrendInfo(0, "Unknown", "Unknown", "Unknown", "No history yet");
        }

        decimal? firstProfit = FirstValue(samples.Select(sample => sample.ProfitPerHour));
        decimal? lastProfit = LastValue(samples.Select(sample => sample.ProfitPerHour));
        decimal? firstMarket = FirstValue(samples.Select(sample => sample.MarketValue));
        decimal? lastMarket = LastValue(samples.Select(sample => sample.MarketValue));
        int firstStock = samples.First().Quantity;
        int lastStock = samples.Last().Quantity;

        return new HistoryTrendInfo(
            samples.Count,
            BuildTrend(firstProfit, lastProfit, "Improving", "Worsening", "Stable"),
            BuildStockTrend(firstStock, lastStock),
            BuildTrend(firstMarket, lastMarket, "Market rising", "Market falling", "Market stable"),
            BuildSelloutBehavior(samples));
    }

    public static IReadOnlyList<TravelDestination> ApplyTrends(
        IReadOnlyList<TravelDestination> destinations,
        IReadOnlyDictionary<string, HistoryTrendInfo> trends)
    {
        return destinations
            .Select(destination => destination with
            {
                Items = destination.Items
                    .Select(item =>
                    {
                        string key = HistoryDatabaseService.BuildTrendKey(item.Id, destination.Code);
                        return trends.TryGetValue(key, out HistoryTrendInfo? trend)
                            ? item with { HistoryTrend = trend }
                            : item;
                    })
                    .ToList()
            })
            .ToList();
    }

    private static decimal? FirstValue(IEnumerable<decimal?> values)
    {
        return values.FirstOrDefault(value => value is not null);
    }

    private static decimal? LastValue(IEnumerable<decimal?> values)
    {
        return values.LastOrDefault(value => value is not null);
    }

    private static string BuildTrend(
        decimal? first,
        decimal? last,
        string risingText,
        string fallingText,
        string stableText)
    {
        if (first is null || last is null || first.Value == 0)
        {
            return "Unknown";
        }

        decimal change = (last.Value - first.Value) / Math.Abs(first.Value);
        return change switch
        {
            >= 0.05m => risingText,
            <= -0.05m => fallingText,
            _ => stableText
        };
    }

    private static string BuildStockTrend(int firstStock, int lastStock)
    {
        if (lastStock > firstStock)
        {
            return "Stock building";
        }

        if (lastStock < firstStock)
        {
            return "Stock draining";
        }

        return "Stock stable";
    }

    private static string BuildSelloutBehavior(IReadOnlyList<HistorySample> samples)
    {
        List<TimeSpan> selloutDurations = new();
        DateTimeOffset? positiveStockStartedAt = null;

        foreach (HistorySample sample in samples)
        {
            if (sample.Quantity > 0 && positiveStockStartedAt is null)
            {
                positiveStockStartedAt = sample.ObservedAt;
            }

            if (sample.Quantity == 0 && positiveStockStartedAt is not null)
            {
                selloutDurations.Add(sample.ObservedAt - positiveStockStartedAt.Value);
                positiveStockStartedAt = null;
            }
        }

        if (selloutDurations.Count == 0)
        {
            return samples.Last().Quantity > 0
                ? "Holding stock"
                : "No sellout pattern";
        }

        TimeSpan average = TimeSpan.FromTicks((long)selloutDurations.Average(duration => duration.Ticks));
        if (average.TotalHours < 2)
        {
            return "Sells out fast";
        }

        if (average.TotalHours < 12)
        {
            return $"Sells out ~{FormatDuration(average)}";
        }

        return "Sells out slowly";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m";
    }
}
