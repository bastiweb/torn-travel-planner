using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;

    public AppSettingsService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDirectory = Path.Combine(appData, "TornStockTravel");
        Directory.CreateDirectory(appDirectory);
        _settingsPath = Path.Combine(appDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings(string.Empty);
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            StoredSettings? storedSettings = JsonSerializer.Deserialize<StoredSettings>(json);
            Dictionary<int, decimal> bazaarPrices = storedSettings?.BazaarPrices ?? new Dictionary<int, decimal>();
            int buyCapacity = storedSettings?.BuyCapacity ?? 0;
            int refreshIntervalMinutes = NormalizeRefreshIntervalMinutes(
                storedSettings?.RefreshIntervalMinutes ?? AppSettings.DefaultRefreshIntervalMinutes);
            int maxSpendPercent = NormalizeMaxSpendPercent(
                storedSettings?.MaxSpendPercent ?? AppSettings.DefaultMaxSpendPercent);
            int departureReminderMinutes = NormalizeDepartureReminderMinutes(
                storedSettings?.DepartureReminderMinutes ?? AppSettings.DefaultDepartureReminderMinutes);
            RestockAvailabilityMode availabilityMode = NormalizeAvailabilityMode(
                storedSettings?.RestockAvailabilityMode);
            IReadOnlyList<PlannerPreset> plannerPresets = NormalizePlannerPresets(storedSettings?.PlannerPresets);
            string discordWebhookUrl = !string.IsNullOrWhiteSpace(storedSettings?.ProtectedDiscordWebhookUrl)
                ? Unprotect(storedSettings.ProtectedDiscordWebhookUrl)
                : storedSettings?.DiscordWebhookUrl ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(storedSettings?.ProtectedTornApiKey))
            {
                return new AppSettings(
                    Unprotect(storedSettings.ProtectedTornApiKey),
                    bazaarPrices,
                    buyCapacity,
                    refreshIntervalMinutes,
                    maxSpendPercent,
                    availabilityMode,
                    storedSettings.LastSeenVersion,
                    storedSettings.PendingReleaseVersion,
                    storedSettings.PendingReleaseNotes,
                    plannerPresets,
                    storedSettings.DepartureRemindersEnabled ?? AppSettings.DefaultDepartureRemindersEnabled,
                    departureReminderMinutes,
                    storedSettings.RestockAlertsEnabled ?? AppSettings.DefaultRestockAlertsEnabled,
                    storedSettings.LandingReminderEnabled ?? AppSettings.DefaultLandingReminderEnabled,
                    storedSettings.NerveNearFullAlertEnabled ?? AppSettings.DefaultNerveNearFullAlertEnabled,
                    storedSettings.DiscordAlertsEnabled ?? AppSettings.DefaultDiscordAlertsEnabled,
                    discordWebhookUrl);
            }

            return new AppSettings(
                storedSettings?.TornApiKey ?? string.Empty,
                bazaarPrices,
                buyCapacity,
                refreshIntervalMinutes,
                maxSpendPercent,
                availabilityMode,
                storedSettings?.LastSeenVersion,
                storedSettings?.PendingReleaseVersion,
                storedSettings?.PendingReleaseNotes,
                plannerPresets,
                storedSettings?.DepartureRemindersEnabled ?? AppSettings.DefaultDepartureRemindersEnabled,
                departureReminderMinutes,
                storedSettings?.RestockAlertsEnabled ?? AppSettings.DefaultRestockAlertsEnabled,
                storedSettings?.LandingReminderEnabled ?? AppSettings.DefaultLandingReminderEnabled,
                storedSettings?.NerveNearFullAlertEnabled ?? AppSettings.DefaultNerveNearFullAlertEnabled,
                storedSettings?.DiscordAlertsEnabled ?? AppSettings.DefaultDiscordAlertsEnabled,
                discordWebhookUrl);
        }
        catch
        {
            return new AppSettings(string.Empty);
        }
    }

    public void Save(AppSettings settings)
    {
        StoredSettings storedSettings = new(
            string.Empty,
            Protect(settings.TornApiKey),
            settings.BazaarPrices.ToDictionary(entry => entry.Key, entry => entry.Value),
            settings.BuyCapacity,
            settings.RefreshIntervalMinutes,
            settings.MaxSpendPercent,
            settings.RestockAvailabilityMode.ToString(),
            settings.LastSeenVersion,
            settings.PendingReleaseVersion,
            settings.PendingReleaseNotes,
            settings.PlannerPresets.Select(preset => new StoredPlannerPreset(
                preset.Name,
                preset.TargetItemQuery,
                preset.ExcludedItemQueries.ToList(),
                preset.ExcludedCountryCodes.ToList(),
                preset.Strategy.ToString(),
                preset.CarryCapacity,
                preset.ActiveWindowHours)).ToList(),
            settings.DepartureRemindersEnabled,
            settings.DepartureReminderMinutes,
            settings.RestockAlertsEnabled,
            settings.LandingReminderEnabled,
            settings.NerveNearFullAlertEnabled,
            settings.DiscordAlertsEnabled,
            string.Empty,
            Protect(settings.DiscordWebhookUrl));
        string json = JsonSerializer.Serialize(storedSettings, JsonOptions.Pretty);
        File.WriteAllText(_settingsPath, json);
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string protectedValue)
    {
        byte[] protectedBytes = Convert.FromBase64String(protectedValue);
        byte[] bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static int NormalizeRefreshIntervalMinutes(int minutes)
    {
        return Math.Clamp(minutes, AppSettings.MinimumRefreshIntervalMinutes, AppSettings.MaximumRefreshIntervalMinutes);
    }

    private static int NormalizeMaxSpendPercent(int percent)
    {
        return Math.Clamp(percent, AppSettings.MinimumMaxSpendPercent, AppSettings.MaximumMaxSpendPercent);
    }

    private static int NormalizeDepartureReminderMinutes(int minutes)
    {
        return Math.Clamp(minutes, AppSettings.MinimumDepartureReminderMinutes, AppSettings.MaximumDepartureReminderMinutes);
    }

    private static RestockAvailabilityMode NormalizeAvailabilityMode(string? mode)
    {
        return Enum.TryParse(mode, ignoreCase: true, out RestockAvailabilityMode parsed)
            ? parsed
            : AppSettings.DefaultRestockAvailabilityMode;
    }

    private static IReadOnlyList<PlannerPreset> NormalizePlannerPresets(List<StoredPlannerPreset>? storedPresets)
    {
        List<PlannerPreset> presets = storedPresets?
            .Where(preset => !string.IsNullOrWhiteSpace(preset.Name))
            .Select(preset => new PlannerPreset(
                preset.Name!.Trim(),
                preset.TargetItemQuery?.Trim() ?? string.Empty,
                (preset.ExcludedItemQueries ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                (preset.ExcludedCountryCodes ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Enum.TryParse(preset.Strategy, ignoreCase: true, out TravelPlannerStrategy strategy)
                    ? strategy
                    : TravelPlannerStrategy.Balanced,
                Math.Max(0, preset.CarryCapacity ?? 0),
                Math.Clamp(preset.ActiveWindowHours ?? 12, 1, 24)))
            .GroupBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList()
            ?? new List<PlannerPreset>();

        return presets.Count > 0 ? presets : GetDefaultPlannerPresets();
    }

    internal static IReadOnlyList<PlannerPreset> GetDefaultPlannerPresets()
    {
        return new[]
        {
            new PlannerPreset("Balanced session", string.Empty, Array.Empty<string>(), Array.Empty<string>(), TravelPlannerStrategy.Balanced, 0, 12),
            new PlannerPreset("Short trips", string.Empty, Array.Empty<string>(), new[] { "haw", "uni", "arg", "swi", "jap", "chi", "uae", "sou" }, TravelPlannerStrategy.Balanced, 0, 4),
            new PlannerPreset("No UAE / South Africa", string.Empty, Array.Empty<string>(), new[] { "uae", "sou" }, TravelPlannerStrategy.Balanced, 0, 12),
            new PlannerPreset("High confidence", string.Empty, Array.Empty<string>(), Array.Empty<string>(), TravelPlannerStrategy.Conservative, 0, 12)
        };
    }
}

public sealed record AppSettings(
    string TornApiKey,
    IReadOnlyDictionary<int, decimal> BazaarPrices,
    int BuyCapacity,
    int RefreshIntervalMinutes,
    int MaxSpendPercent,
    RestockAvailabilityMode RestockAvailabilityMode,
    string? LastSeenVersion,
    string? PendingReleaseVersion,
    string? PendingReleaseNotes,
    IReadOnlyList<PlannerPreset> PlannerPresets,
    bool DepartureRemindersEnabled,
    int DepartureReminderMinutes,
    bool RestockAlertsEnabled,
    bool LandingReminderEnabled,
    bool NerveNearFullAlertEnabled,
    bool DiscordAlertsEnabled,
    string DiscordWebhookUrl)
{
    public const int DefaultRefreshIntervalMinutes = 5;
    public const int MinimumRefreshIntervalMinutes = 1;
    public const int MaximumRefreshIntervalMinutes = 1440;
    public const int DefaultMaxSpendPercent = 50;
    public const int MinimumMaxSpendPercent = 1;
    public const int MaximumMaxSpendPercent = 100;
    public const RestockAvailabilityMode DefaultRestockAvailabilityMode = RestockAvailabilityMode.Conservative;
    public const bool DefaultDepartureRemindersEnabled = false;
    public const int DefaultDepartureReminderMinutes = 5;
    public const int MinimumDepartureReminderMinutes = 1;
    public const int MaximumDepartureReminderMinutes = 120;
    public const bool DefaultRestockAlertsEnabled = false;
    public const bool DefaultLandingReminderEnabled = false;
    public const bool DefaultNerveNearFullAlertEnabled = false;
    public const bool DefaultDiscordAlertsEnabled = false;

    public AppSettings(string TornApiKey)
        : this(
            TornApiKey,
            new Dictionary<int, decimal>(),
            0,
            DefaultRefreshIntervalMinutes,
            DefaultMaxSpendPercent,
            DefaultRestockAvailabilityMode,
            null,
            null,
            null,
            AppSettingsService.GetDefaultPlannerPresets(),
            DefaultDepartureRemindersEnabled,
            DefaultDepartureReminderMinutes,
            DefaultRestockAlertsEnabled,
            DefaultLandingReminderEnabled,
            DefaultNerveNearFullAlertEnabled,
            DefaultDiscordAlertsEnabled,
            string.Empty)
    {
    }
}

internal sealed record StoredSettings(
    string? TornApiKey,
    string? ProtectedTornApiKey,
    Dictionary<int, decimal>? BazaarPrices,
    int? BuyCapacity,
    int? RefreshIntervalMinutes,
    int? MaxSpendPercent,
    string? RestockAvailabilityMode,
    string? LastSeenVersion,
    string? PendingReleaseVersion,
    string? PendingReleaseNotes,
    List<StoredPlannerPreset>? PlannerPresets,
    bool? DepartureRemindersEnabled,
    int? DepartureReminderMinutes,
    bool? RestockAlertsEnabled,
    bool? LandingReminderEnabled,
    bool? NerveNearFullAlertEnabled,
    bool? DiscordAlertsEnabled,
    string? DiscordWebhookUrl,
    string? ProtectedDiscordWebhookUrl);

internal sealed record StoredPlannerPreset(
    string? Name,
    string? TargetItemQuery,
    List<string>? ExcludedItemQueries,
    List<string>? ExcludedCountryCodes,
    string? Strategy,
    int? CarryCapacity,
    int? ActiveWindowHours);
