namespace TornStockTravel.Services;

public sealed record TornBarsStatus(
    TornBarStatus Energy,
    TornBarStatus Nerve);

public sealed record TornBarStatus(
    int Current,
    int Maximum,
    int Interval,
    int TickTime,
    int FullTime)
{
    public bool IsAlreadyFullOrOver => Current >= Maximum;

    public bool WouldFillDuring(TimeSpan duration)
    {
        return !IsAlreadyFullOrOver
            && FullTime > 0
            && FullTime <= duration.TotalSeconds;
    }

    public string ValueText => $"{Current:N0}/{Maximum:N0}";

    public string FullInText => FullTime <= 0 ? "-" : FormatDuration(TimeSpan.FromSeconds(FullTime));

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{duration.Minutes}m";
    }
}
