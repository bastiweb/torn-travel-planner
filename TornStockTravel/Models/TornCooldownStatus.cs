namespace TornStockTravel.Services;

public sealed record TornCooldownStatus(
    int DrugSeconds,
    DateTimeOffset FetchedAt)
{
    public bool HasDrugCooldown => DrugSeconds > 0;

    public DateTimeOffset? DrugEndsAt => HasDrugCooldown
        ? FetchedAt.AddSeconds(DrugSeconds)
        : null;

    public string DrugText => DrugEndsAt is null
        ? "Drug cooldown ready"
        : $"Drug cooldown ends at {DrugEndsAt.Value.LocalDateTime:HH:mm}";
}
