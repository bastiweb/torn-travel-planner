namespace TornStockTravel.Services;

public static class TravelPlannerService
{
    private static readonly TimeSpan LocalStayDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConservativeTimingBuffer = TimeSpan.FromMinutes(2);
    private const string CurrentStockTripType = "Current stock";
    private const string FutureRestockTripType = "Future restock";
    private const string ForecastTripType = "Forecast window";
    private const int MaximumSessionTrips = 16;

    public static TravelPlannerSuggestions GetSuggestions(
        IReadOnlyList<TravelDestination> destinations,
        TornTravelStatus? travelStatus,
        TornMoneyStatus? moneyStatus,
        TornBarsStatus? barsStatus,
        DroqsForecastSnapshot? forecastSnapshot,
        int maxSpendPercent,
        TravelPlannerStrategy strategy,
        DateTimeOffset now)
    {
        DateTimeOffset readyAt = GetNextTornAvailability(travelStatus, now);
        decimal? spendLimit = moneyStatus is null
            ? null
            : moneyStatus.Total * maxSpendPercent / 100m;

        List<TravelPlanCandidate> candidates = RankCandidates(
            BuildCandidates(
                destinations,
                readyAt,
                moneyStatus?.Wallet,
                spendLimit,
                maxSpendPercent,
                barsStatus,
                forecastSnapshot,
                strategy,
                TravelPlannerFilters.Empty,
                now,
                null,
                null,
                null),
            strategy);

        TravelPlanCandidate? bestCandidate = candidates.FirstOrDefault();
        TravelPlanCandidate? energyWasteCandidate = barsStatus?.Energy.IsAlreadyFullOrOver == true
            ? bestCandidate
            : candidates.FirstOrDefault(candidate => candidate.EnergyWaste && !candidate.NerveWaste)
                ?? candidates.FirstOrDefault(candidate => candidate.EnergyWaste);

        return new TravelPlannerSuggestions(
            candidates.FirstOrDefault(candidate => !candidate.EnergyWaste && !candidate.NerveWaste),
            energyWasteCandidate,
            candidates.FirstOrDefault(candidate => candidate.NerveWaste && !candidate.EnergyWaste)
                ?? candidates.FirstOrDefault(candidate => candidate.NerveWaste));
    }

    public static TravelPlannerSessionPlan GetSessionPlan(
        IReadOnlyList<TravelDestination> destinations,
        TornTravelStatus? travelStatus,
        TornMoneyStatus? moneyStatus,
        TornBarsStatus? barsStatus,
        DroqsForecastSnapshot? forecastSnapshot,
        int maxSpendPercent,
        TravelPlannerStrategy strategy,
        TravelPlannerFilters? filters,
        DateTimeOffset now,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TornCooldownStatus? cooldownStatus = null,
        int? plannerBuyCapacity = null,
        IReadOnlyDictionary<string, int>? manualReservedCurrentStock = null)
    {
        if (windowEnd <= windowStart)
        {
            return new TravelPlannerSessionPlan(windowStart, windowEnd, Array.Empty<TravelPlanCandidate>(), "Active window must end after it starts.");
        }

        DateTimeOffset firstReadyAt = Max(windowStart, GetNextTornAvailability(travelStatus, now));
        if (firstReadyAt >= windowEnd)
        {
            return new TravelPlannerSessionPlan(windowStart, windowEnd, Array.Empty<TravelPlanCandidate>(), "No time left in the active window.");
        }

        decimal? spendLimit = moneyStatus is null
            ? null
            : moneyStatus.Total * maxSpendPercent / 100m;
        List<TravelPlanCandidate> trips = new();
        Dictionary<string, int> reservedCurrentStock = manualReservedCurrentStock is null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int>(manualReservedCurrentStock, StringComparer.OrdinalIgnoreCase);
        DateTimeOffset readyAt = firstReadyAt;
        decimal plannedCashNeeded = 0;

        for (int index = 1; index <= MaximumSessionTrips && readyAt < windowEnd; index++)
        {
            decimal? remainingSpendLimit = spendLimit is null
                ? null
                : Math.Max(0, spendLimit.Value - plannedCashNeeded);
            if (remainingSpendLimit == 0)
            {
                break;
            }

            List<TravelPlanCandidate> candidates = RankCandidates(
                BuildCandidates(
                    destinations,
                    readyAt,
                    moneyStatus?.Wallet,
                    remainingSpendLimit,
                    maxSpendPercent,
                    barsStatus,
                    forecastSnapshot,
                    strategy,
                    filters ?? TravelPlannerFilters.Empty,
                    now,
                    windowEnd,
                    plannerBuyCapacity,
                    reservedCurrentStock),
                strategy);

            TravelPlanCandidate? selected = candidates.FirstOrDefault();
            if (selected is null)
            {
                break;
            }

            selected = AddCooldownContext(selected, candidates, cooldownStatus);

            trips.Add(selected with
            {
                Category = $"Trip {index}"
            });
            if (selected.Stock > 0)
            {
                string key = BuildReservationKey(selected.DestinationCode, selected.ItemName);
                reservedCurrentStock[key] = reservedCurrentStock.TryGetValue(key, out int reserved)
                    ? reserved + selected.BuyAmount
                    : selected.BuyAmount;
            }

            plannedCashNeeded += selected.CashNeeded;
            readyAt = selected.ReturnAt;
        }

        string statusText = trips.Count == 0
            ? "No profitable trip fits this active window."
            : "Session plan updated from current stock, restocks, wallet, bars, and strategy.";

        return new TravelPlannerSessionPlan(windowStart, windowEnd, trips, statusText);
    }

    private static IEnumerable<TravelPlanCandidate> BuildCandidates(
        IReadOnlyList<TravelDestination> destinations,
        DateTimeOffset readyAt,
        decimal? wallet,
        decimal? spendLimit,
        int maxSpendPercent,
        TornBarsStatus? barsStatus,
        DroqsForecastSnapshot? forecastSnapshot,
        TravelPlannerStrategy strategy,
        TravelPlannerFilters filters,
        DateTimeOffset now,
        DateTimeOffset? sessionEnd,
        int? plannerBuyCapacity,
        IReadOnlyDictionary<string, int>? reservedCurrentStock = null)
    {
        return destinations
            .Where(destination => !filters.ExcludedCountryCodes.Contains(destination.Code))
            .SelectMany(destination => destination.Items.Select(item =>
            {
                if (!IsItemAllowed(item, filters))
                {
                    return null;
                }

                TravelItem adjustedItem = ApplyPlannerCapacity(item, plannerBuyCapacity);
                adjustedItem = AdjustForReservedStock(destination, adjustedItem, reservedCurrentStock);
                DroqsForecastInfo? forecastInfo = GetForecastInfo(forecastSnapshot, destination, adjustedItem);
                return BuildCandidate(
                    destination,
                    adjustedItem,
                    forecastInfo,
                    readyAt,
                    wallet,
                    spendLimit,
                    maxSpendPercent,
                    barsStatus,
                    strategy,
                    now,
                    sessionEnd);
            }))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!);
    }

    private static List<TravelPlanCandidate> RankCandidates(
        IEnumerable<TravelPlanCandidate> candidates,
        TravelPlannerStrategy strategy)
    {
        IOrderedEnumerable<TravelPlanCandidate> ranked = strategy switch
        {
            TravelPlannerStrategy.Conservative => candidates
                .OrderBy(candidate => candidate.EnergyWaste || candidate.NerveWaste)
                .ThenByDescending(candidate => GetConfidenceScore(candidate.ConfidenceText))
                .ThenByDescending(candidate => candidate.ProfitPerHour)
                .ThenByDescending(candidate => candidate.Profit)
                .ThenBy(candidate => candidate.DepartAt),
            TravelPlannerStrategy.Aggressive => candidates
                .OrderByDescending(candidate => candidate.ProfitPerHour)
                .ThenByDescending(candidate => candidate.Profit)
                .ThenBy(candidate => candidate.DepartAt),
            _ => candidates
                .OrderByDescending(candidate => candidate.ProfitPerHour)
                .ThenBy(candidate => candidate.EnergyWaste || candidate.NerveWaste)
                .ThenByDescending(candidate => GetConfidenceScore(candidate.ConfidenceText))
                .ThenByDescending(candidate => candidate.Profit)
                .ThenBy(candidate => candidate.DepartAt)
        };

        return ranked.ToList();
    }

    private static TravelPlanCandidate? BuildCandidate(
        TravelDestination destination,
        TravelItem item,
        DroqsForecastInfo? forecastInfo,
        DateTimeOffset readyAt,
        decimal? wallet,
        decimal? spendLimit,
        int maxSpendPercent,
        TornBarsStatus? barsStatus,
        TravelPlannerStrategy strategy,
        DateTimeOffset now,
        DateTimeOffset? sessionEnd)
    {
        if (item.FlightDuration is null
            || item.Profit is null
            || item.Profit <= 0
            || item.BuyAmount <= 0)
        {
            return null;
        }

        if (spendLimit is not null && item.CashNeeded > spendLimit.Value)
        {
            return null;
        }

        DateTimeOffset departAt;
        string tripType;
        string reason;
        string timingText;
        string availabilityText;
        string confidenceText;
        bool usesForecast;

        TravelPlanCandidate? forecastCandidate = BuildForecastCandidate(
            destination,
            item,
            forecastInfo,
            readyAt,
            wallet,
            maxSpendPercent,
            barsStatus,
            strategy,
            now,
            sessionEnd);
        if (forecastCandidate is not null)
        {
            return forecastCandidate;
        }

        if (item.Quantity > 0)
        {
            departAt = readyAt;
            DateTimeOffset arriveAt = departAt.Add(item.FlightDuration.Value);

            if (!CanArriveBeforeStockout(item, arriveAt, strategy))
            {
                return null;
            }

            tripType = CurrentStockTripType;
            reason = "Stock should still be available when you arrive";
            timingText = item.RestockEstimateLabelText;
            availabilityText = item.StockoutEstimateLabelText;
            confidenceText = BuildConfidenceText(item);
            usesForecast = false;
        }
        else if (item.RestockEstimateUtc is not null)
        {
            DateTimeOffset restockAt = item.RestockEstimateUtc.Value;
            TimeSpan arrivalLead = strategy == TravelPlannerStrategy.Aggressive
                ? TimeSpan.Zero
                : ConservativeTimingBuffer;
            TimeSpan availabilityDuration = GetAvailabilityDuration(item, strategy) ?? ConservativeTimingBuffer;
            DateTimeOffset targetArrival = restockAt.Subtract(arrivalLead);
            DateTimeOffset latestArrival = restockAt.Add(availabilityDuration);
            DateTimeOffset latestDeparture = latestArrival.Subtract(item.FlightDuration.Value);

            if (readyAt > latestDeparture)
            {
                return null;
            }

            departAt = Max(readyAt, targetArrival.Subtract(item.FlightDuration.Value));
            tripType = FutureRestockTripType;
            reason = "Restock window should fit your arrival";
            timingText = item.RestockEstimateLabelText;
            availabilityText = item.StockoutEstimateLabelText;
            confidenceText = BuildConfidenceText(item);
            usesForecast = false;
        }
        else
        {
            return null;
        }

        DateTimeOffset arrivalAt = departAt.Add(item.FlightDuration.Value);
        DateTimeOffset returnAt = arrivalAt.Add(LocalStayDuration).Add(item.FlightDuration.Value);
        if (sessionEnd is not null && returnAt > sessionEnd.Value)
        {
            return null;
        }

        TimeSpan timeAway = returnAt.Subtract(departAt);
        decimal profitPerHour = timeAway.TotalHours <= 0
            ? 0
            : item.Profit.Value / (decimal)timeAway.TotalHours;

        if (profitPerHour <= 0)
        {
            return null;
        }

        bool energyWaste = WouldCapDuringTrip(barsStatus?.Energy, now, departAt, returnAt);
        bool nerveWaste = WouldCapDuringTrip(barsStatus?.Nerve, now, departAt, returnAt);

        return new TravelPlanCandidate(
            string.Empty,
            destination.Name,
            destination.Code,
            item.Name,
            tripType,
            reason,
            departAt,
            arrivalAt,
            returnAt,
            timeAway,
            item.Profit.Value,
            profitPerHour,
            item.CashNeeded,
            item.BuyAmount,
            item.Quantity,
            timingText,
            availabilityText,
            confidenceText,
            BuildStrategyExplanation(strategy, tripType, usesForecast, profitPerHour, energyWaste, nerveWaste),
            string.Empty,
            string.Empty,
            usesForecast,
            energyWaste,
            nerveWaste,
            wallet,
            maxSpendPercent);
    }

    private static TravelPlanCandidate? BuildForecastCandidate(
        TravelDestination destination,
        TravelItem item,
        DroqsForecastInfo? forecastInfo,
        DateTimeOffset readyAt,
        decimal? wallet,
        int maxSpendPercent,
        TornBarsStatus? barsStatus,
        TravelPlannerStrategy strategy,
        DateTimeOffset now,
        DateTimeOffset? sessionEnd)
    {
        if (forecastInfo is null
            || item.FlightDuration is null
            || item.Profit is null
            || item.Profit <= 0
            || item.BuyAmount <= 0)
        {
            return null;
        }

        ForecastWindowMatch? match = forecastInfo.FlyOutWindows
            .Where(window => IsForecastWindowAllowed(window, strategy))
            .Select(window => new ForecastWindowMatch(window, BuildForecastWindowTiming(window, readyAt, item.FlightDuration.Value)))
            .Where(match => match.Timing is not null)
            .OrderBy(match => match.Timing!.DepartAt)
            .ThenByDescending(match => match.Window.ConfidencePercent ?? 0)
            .ThenByDescending(match => match.Window.SafetyMarginMinutes ?? 0)
            .FirstOrDefault();

        if (match?.Timing is null)
        {
            return null;
        }

        ForecastWindowTiming timing = match.Timing;
        DateTimeOffset returnAt = timing.ArriveAt.Add(LocalStayDuration).Add(item.FlightDuration.Value);
        if (sessionEnd is not null && returnAt > sessionEnd.Value)
        {
            return null;
        }

        TimeSpan timeAway = returnAt.Subtract(timing.DepartAt);
        decimal profitPerHour = timeAway.TotalHours <= 0
            ? 0
            : item.Profit.Value / (decimal)timeAway.TotalHours;

        if (profitPerHour <= 0)
        {
            return null;
        }

        bool energyWaste = WouldCapDuringTrip(barsStatus?.Energy, now, timing.DepartAt, returnAt);
        bool nerveWaste = WouldCapDuringTrip(barsStatus?.Nerve, now, timing.DepartAt, returnAt);

        return new TravelPlanCandidate(
            string.Empty,
            destination.Name,
            destination.Code,
            item.Name,
            ForecastTripType,
            BuildForecastReason(match.Window),
            timing.DepartAt,
            timing.ArriveAt,
            returnAt,
            timeAway,
            item.Profit.Value,
            profitPerHour,
            item.CashNeeded,
            item.BuyAmount,
            item.Quantity,
            BuildForecastTimingText(match.Window, timing),
            BuildForecastSafetyText(match.Window),
            BuildForecastConfidenceText(forecastInfo, match.Window),
            BuildStrategyExplanation(strategy, ForecastTripType, usesForecast: true, profitPerHour, energyWaste, nerveWaste),
            string.Empty,
            string.Empty,
            true,
            energyWaste,
            nerveWaste,
            wallet,
            maxSpendPercent);
    }

    private static ForecastWindowTiming? BuildForecastWindowTiming(
        DroqsForecastWindow window,
        DateTimeOffset readyAt,
        TimeSpan flightDuration)
    {
        DateTimeOffset? leaveAt = window.LeaveAt;
        DateTimeOffset? leaveWindowEndAt = window.LeaveWindowEndAt ?? window.LeaveAt;
        if (leaveAt is null || leaveWindowEndAt is null)
        {
            return null;
        }

        DateTimeOffset earliestDepart = Max(readyAt, leaveAt.Value);
        if (window.AvailabilityStartsAt is not null)
        {
            earliestDepart = Max(earliestDepart, window.AvailabilityStartsAt.Value.Subtract(flightDuration));
        }

        DateTimeOffset latestDepart = leaveWindowEndAt.Value;
        if (window.AvailabilityEndsAt is not null)
        {
            latestDepart = Min(latestDepart, window.AvailabilityEndsAt.Value.Subtract(flightDuration));
        }

        if (earliestDepart > latestDepart)
        {
            return null;
        }

        DateTimeOffset arriveAt = earliestDepart.Add(flightDuration);
        if (window.ArrivalWindowEndAt is not null && arriveAt > window.ArrivalWindowEndAt.Value)
        {
            return null;
        }

        return new ForecastWindowTiming(earliestDepart, arriveAt);
    }

    private static bool IsForecastWindowAllowed(DroqsForecastWindow window, TravelPlannerStrategy strategy)
    {
        int confidencePercent = window.ConfidencePercent ?? 0;
        int safetyMargin = window.SafetyMarginMinutes ?? 0;

        return strategy switch
        {
            TravelPlannerStrategy.Conservative =>
                !window.TightWindow
                && GetConfidenceScore(window.Confidence ?? string.Empty) >= 3
                && confidencePercent >= 75
                && safetyMargin >= 15,
            TravelPlannerStrategy.Aggressive =>
                confidencePercent >= 40 || GetConfidenceScore(window.Confidence ?? string.Empty) >= 1,
            _ =>
                !window.TightWindow
                || confidencePercent >= 70
                || safetyMargin >= 20
        };
    }

    private static string BuildForecastReason(DroqsForecastWindow window)
    {
        string availability = FormatAvailability(window.Availability);

        return window.TightWindow
            ? $"Droqs forecast shows a tight {availability} window."
            : $"Droqs forecast shows a {availability} window for arrival.";
    }

    private static string BuildForecastTimingText(DroqsForecastWindow window, ForecastWindowTiming timing)
    {
        string departWindow = window.LeaveWindowEndAt is null
            ? FormatLocalTime(timing.DepartAt)
            : $"{FormatLocalTime(timing.DepartAt)}-{FormatLocalTime(window.LeaveWindowEndAt.Value)}";

        return $"Forecast depart window: {departWindow}";
    }

    private static string BuildForecastSafetyText(DroqsForecastWindow window)
    {
        string safety = window.SafetyMarginMinutes is null
            ? "Safety margin: -"
            : $"Safety margin: {FormatMinutes(window.SafetyMarginMinutes.Value)}";
        string availability = string.IsNullOrWhiteSpace(window.Availability)
            ? string.Empty
            : $" | {FormatAvailability(window.Availability)}";
        string tight = window.TightWindow ? " | tight window" : string.Empty;

        return $"{safety}{availability}{tight}";
    }

    private static string BuildForecastConfidenceText(
        DroqsForecastInfo forecastInfo,
        DroqsForecastWindow window)
    {
        string windowConfidence = window.ConfidencePercent is null
            ? window.Confidence ?? "-"
            : $"{window.Confidence ?? "confidence"} ({window.ConfidencePercent.Value:N0}%)";
        string itemConfidence = forecastInfo.ConfidencePercent is null
            ? forecastInfo.Confidence ?? "-"
            : $"{forecastInfo.Confidence ?? "item"} ({forecastInfo.ConfidencePercent.Value:N0}%)";

        return $"Forecast confidence: {windowConfidence} | Item model: {itemConfidence}";
    }

    private static bool WouldCapDuringTrip(
        TornBarStatus? barStatus,
        DateTimeOffset now,
        DateTimeOffset departAt,
        DateTimeOffset returnAt)
    {
        if (barStatus is null
            || barStatus.IsAlreadyFullOrOver
            || barStatus.FullTime <= 0)
        {
            return false;
        }

        DateTimeOffset fullAt = now.AddSeconds(barStatus.FullTime);
        return fullAt > departAt && fullAt <= returnAt;
    }

    private static bool CanArriveBeforeStockout(
        TravelItem item,
        DateTimeOffset arrivalAt,
        TravelPlannerStrategy strategy)
    {
        if (item.StockoutEstimateUtc is null)
        {
            return true;
        }

        DateTimeOffset stockoutAt = strategy == TravelPlannerStrategy.Conservative
            ? item.StockoutEstimateUtc.Value.Subtract(ConservativeTimingBuffer)
            : item.StockoutEstimateUtc.Value;

        return arrivalAt <= stockoutAt;
    }

    private static TimeSpan? GetAvailabilityDuration(TravelItem item, TravelPlannerStrategy strategy)
    {
        RestockAvailabilityMode mode = strategy == TravelPlannerStrategy.Conservative
            ? RestockAvailabilityMode.Conservative
            : RestockAvailabilityMode.Median;

        return item.RestockInfo?.GetAvailabilityDuration(mode);
    }

    private static DateTimeOffset GetNextTornAvailability(TornTravelStatus? travelStatus, DateTimeOffset now)
    {
        return TravelAvailabilityService.GetAvailability(travelStatus, now).ReadyInTornAt;
    }

    private static string BuildConfidenceText(TravelItem item)
    {
        return item.StockoutConfidenceText == "-"
            ? item.RestockConfidenceLabelText
            : $"{item.RestockConfidenceLabelText} | {item.StockoutConfidenceLabelText}";
    }

    private static int GetConfidenceScore(string confidenceText)
    {
        if (confidenceText.Contains("high", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (confidenceText.Contains("medium", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (confidenceText.Contains("low", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    private static TravelItem AdjustForReservedStock(
        TravelDestination destination,
        TravelItem item,
        IReadOnlyDictionary<string, int>? reservedCurrentStock)
    {
        if (reservedCurrentStock is null
            || item.Quantity <= 0
            || !reservedCurrentStock.TryGetValue(BuildReservationKey(destination.Code, item.Name), out int reserved)
            || reserved <= 0)
        {
            return item;
        }

        return item with
        {
            Quantity = Math.Max(0, item.Quantity - reserved)
        };
    }

    private static TravelItem ApplyPlannerCapacity(TravelItem item, int? plannerBuyCapacity)
    {
        if (plannerBuyCapacity is null || plannerBuyCapacity.Value <= 0 || plannerBuyCapacity.Value == item.BuyCapacity)
        {
            return item;
        }

        return item with
        {
            BuyCapacity = plannerBuyCapacity.Value
        };
    }

    private static string BuildStrategyExplanation(
        TravelPlannerStrategy strategy,
        string tripType,
        bool usesForecast,
        decimal profitPerHour,
        bool energyWaste,
        bool nerveWaste)
    {
        string strategyText = strategy switch
        {
            TravelPlannerStrategy.Conservative => "Conservative strategy favors safer timing, confidence, and avoiding capped bars.",
            TravelPlannerStrategy.Aggressive => "Aggressive strategy prioritizes the highest profit per hour.",
            _ => "Balanced strategy prioritizes profit per hour, then avoids energy or nerve waste when possible."
        };
        string timingText = usesForecast
            ? "Uses a Droqs forecast window."
            : tripType == CurrentStockTripType
                ? "Uses currently available stock."
                : "Uses the next expected restock.";
        string wasteText = energyWaste || nerveWaste
            ? "Bars may cap during the round trip."
            : "No energy or nerve cap is expected.";

        return $"Chosen because: ${profitPerHour:N0}/h. {strategyText} {timingText} {wasteText}";
    }

    private static TravelPlanCandidate AddCooldownContext(
        TravelPlanCandidate selected,
        IReadOnlyList<TravelPlanCandidate> candidates,
        TornCooldownStatus? cooldownStatus)
    {
        DateTimeOffset? drugEndsAt = cooldownStatus?.DrugEndsAt;
        if (drugEndsAt is null
            || drugEndsAt.Value <= selected.DepartAt
            || drugEndsAt.Value > selected.ReturnAt)
        {
            return selected;
        }

        DateTimeOffset latestGoodReturn = drugEndsAt.Value.AddMinutes(10);
        TravelPlanCandidate? alternative = candidates
            .Where(candidate => !ReferenceEquals(candidate, selected))
            .Where(candidate => candidate.ReturnAt >= drugEndsAt.Value && candidate.ReturnAt <= latestGoodReturn)
            .OrderByDescending(candidate => candidate.ProfitPerHour)
            .ThenByDescending(candidate => candidate.Profit)
            .FirstOrDefault();

        string cooldownText = $"Drug cooldown ends during this trip at {drugEndsAt.Value.LocalDateTime:HH:mm}.";
        string alternativeText = alternative is null
            ? "No alternative route returns within 10 minutes after drug cooldown."
            : $"Alternative: {alternative.DestinationText} - {alternative.ItemName}, return {alternative.ReturnText}, ${alternative.ProfitPerHourText}/h.";

        return selected with
        {
            CooldownText = cooldownText,
            AlternativeRouteText = alternativeText
        };
    }

    private static bool IsItemAllowed(TravelItem item, TravelPlannerFilters filters)
    {
        if (!string.IsNullOrWhiteSpace(filters.TargetItemQuery)
            && !item.Name.Contains(filters.TargetItemQuery, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        return !filters.ExcludedItemQueries.Any(excluded =>
            item.Name.Contains(excluded, StringComparison.CurrentCultureIgnoreCase));
    }

    private static DroqsForecastInfo? GetForecastInfo(
        DroqsForecastSnapshot? forecastSnapshot,
        TravelDestination destination,
        TravelItem item)
    {
        return forecastSnapshot is null || item.Id <= 0
            ? null
            : forecastSnapshot.Items.TryGetValue(DroqsRestockClient.BuildKey(item.Id, destination.Code), out DroqsForecastInfo? forecastInfo)
                ? forecastInfo
                : null;
    }

    private static string FormatLocalTime(DateTimeOffset value)
    {
        return value.LocalDateTime.ToString("HH:mm");
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes >= 60)
        {
            return $"{minutes / 60}h {minutes % 60}m";
        }

        return $"{minutes}m";
    }

    private static string FormatAvailability(string? availability)
    {
        return string.IsNullOrWhiteSpace(availability)
            ? "availability"
            : availability.Replace('_', ' ');
    }

    public static string BuildReservationKey(string destinationCode, string itemName)
    {
        return $"{destinationCode}:{itemName}";
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second)
    {
        return first <= second ? first : second;
    }

    private sealed record ForecastWindowMatch(
        DroqsForecastWindow Window,
        ForecastWindowTiming? Timing);

    private sealed record ForecastWindowTiming(
        DateTimeOffset DepartAt,
        DateTimeOffset ArriveAt);
}
