namespace TornStockTravel.Services;

public enum TravelPlannerStrategy
{
    Conservative,
    Balanced,
    Aggressive
}

public sealed record TravelPlannerSuggestions(
    TravelPlanCandidate? NoWaste,
    TravelPlanCandidate? EnergyWaste,
    TravelPlanCandidate? NerveWaste);

public sealed record TravelPlannerFilters(
    string TargetItemQuery,
    IReadOnlyList<string> ExcludedItemQueries,
    IReadOnlySet<string> ExcludedCountryCodes)
{
    public static TravelPlannerFilters Empty { get; } = new(
        string.Empty,
        Array.Empty<string>(),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

public sealed record PlannerPreset(
    string Name,
    string TargetItemQuery,
    IReadOnlyList<string> ExcludedItemQueries,
    IReadOnlyList<string> ExcludedCountryCodes,
    TravelPlannerStrategy Strategy,
    int CarryCapacity,
    int ActiveWindowHours)
{
    public override string ToString()
    {
        return Name;
    }
}

public sealed record TravelPlannerSessionPlan(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    IReadOnlyList<TravelPlanCandidate> Trips,
    string StatusText)
{
    public decimal TotalProfit => Trips.Sum(trip => trip.Profit);

    public decimal TotalCashNeeded => Trips.Sum(trip => trip.CashNeeded);

    public TimeSpan PlannedTimeAway => TimeSpan.FromSeconds(Trips.Sum(trip => trip.TimeAway.TotalSeconds));

    public string SummaryText => Trips.Count == 0
        ? StatusText
        : $"{Trips.Count} trips | ${TotalProfit:N0} expected profit | ${TotalCashNeeded:N0} cash planned | {FormatDuration(PlannedTimeAway)} away";

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m";
    }
}

public sealed record TravelPlanCandidate(
    string Category,
    string DestinationName,
    string DestinationCode,
    string ItemName,
    string TripType,
    string Reason,
    DateTimeOffset DepartAt,
    DateTimeOffset ArriveAt,
    DateTimeOffset ReturnAt,
    TimeSpan TimeAway,
    decimal Profit,
    decimal ProfitPerHour,
    decimal CashNeeded,
    int BuyAmount,
    int Stock,
    string RestockText,
    string StockoutText,
    string ConfidenceText,
    string StrategyExplanationText,
    string CooldownText,
    string AlternativeRouteText,
    bool UsesForecast,
    bool EnergyWaste,
    bool NerveWaste,
    decimal? Wallet,
    int MaxSpendPercent)
{
    public string DestinationText => $"{DestinationName} ({DestinationCode.ToUpperInvariant()})";

    public string DepartText => FormatLocalTime(DepartAt);

    public string ArriveText => FormatLocalTime(ArriveAt);

    public string ReturnText => FormatLocalTime(ReturnAt);

    public string TimeAwayText => FormatDuration(TimeAway);

    public string ProfitText => Profit.ToString("N0");

    public string ProfitPerHourText => ProfitPerHour.ToString("N0");

    public string CashNeededText => CashNeeded.ToString("N0");

    public string BuyAmountText => BuyAmount.ToString("N0");

    public string StockText => Stock.ToString("N0");

    public string TimingSourceText => UsesForecast ? "Droqs forecast timing" : "Dashboard timing";

    public bool HasEnoughWalletCash => Wallet is null || Wallet.Value >= CashNeeded;

    public decimal WalletShortfall => Wallet is null ? 0 : Math.Max(0, CashNeeded - Wallet.Value);

    public string CashStatusText => Wallet is null
        ? $"Within {MaxSpendPercent}% budget"
        : HasEnoughWalletCash
            ? "Wallet covers this trip"
            : $"Withdraw ${WalletShortfall:N0}";

    public string WalletReadinessText => Wallet is null
        ? $"Bring ${CashNeeded:N0} | Wallet unavailable | Within {MaxSpendPercent}% limit"
        : HasEnoughWalletCash
            ? $"Bring ${CashNeeded:N0} | Wallet ready | Within {MaxSpendPercent}% limit"
            : $"Bring ${CashNeeded:N0} | Wallet short by ${WalletShortfall:N0} | Within {MaxSpendPercent}% limit";

    public string WasteText => (EnergyWaste, NerveWaste) switch
    {
        (false, false) => "No energy or nerve waste",
        (true, false) => "Possible energy waste",
        (false, true) => "Possible nerve waste",
        (true, true) => "Possible energy + nerve waste"
    };

    private static string FormatLocalTime(DateTimeOffset value)
    {
        return value.LocalDateTime.ToString("HH:mm");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.Minutes}m";
    }
}

public sealed record TravelPlanCard(
    string Title,
    string EmptyText,
    TravelPlanCandidate? Candidate)
{
    public bool HasCandidate => Candidate is not null;

    public string DestinationText => Candidate?.DestinationText ?? "No matching trip";

    public string ItemName => Candidate?.ItemName ?? EmptyText;

    public string TripType => Candidate?.TripType ?? "-";

    public string WasteText => Candidate?.WasteText ?? "-";

    public string DepartText => Candidate?.DepartText ?? "-";

    public string ArriveText => Candidate?.ArriveText ?? "-";

    public string ReturnText => Candidate?.ReturnText ?? "-";

    public string TimeAwayText => Candidate?.TimeAwayText ?? "-";

    public string ProfitText => Candidate?.ProfitText ?? "-";

    public string ProfitDollarText => Candidate is null ? "-" : $"${Candidate.ProfitText}";

    public string ProfitPerHourText => Candidate?.ProfitPerHourText ?? "-";

    public string ProfitPerHourDollarText => Candidate is null ? "-" : $"${Candidate.ProfitPerHourText}";

    public string CashNeededText => Candidate?.CashNeededText ?? "-";

    public string CashNeededDollarText => Candidate is null ? "-" : $"${Candidate.CashNeededText}";

    public string BuyAmountText => Candidate?.BuyAmountText ?? "-";

    public string StockText => Candidate?.StockText ?? "-";

    public string RestockText => Candidate?.RestockText ?? "-";

    public string StockoutText => Candidate?.StockoutText ?? "-";

    public string ConfidenceText => Candidate?.ConfidenceText ?? "-";

    public string TimingSourceText => Candidate?.TimingSourceText ?? "-";

    public string CashStatusText => Candidate?.CashStatusText ?? "-";

    public string WalletReadinessText => Candidate?.WalletReadinessText ?? "-";

    public string StrategyExplanationText => Candidate?.StrategyExplanationText ?? "-";

    public string CooldownText => Candidate?.CooldownText ?? string.Empty;

    public string AlternativeRouteText => Candidate?.AlternativeRouteText ?? string.Empty;

    public bool CanMarkBought => Candidate is not null && Candidate.Stock > 0 && Candidate.BuyAmount > 0;

    public string Reason => Candidate?.Reason ?? EmptyText;
}

public sealed record TravelPlanTimelineEntry(
    string Title,
    string ItemText,
    string DepartText,
    string ArriveText,
    string ReturnText,
    string ProfitText,
    string WalletText);
