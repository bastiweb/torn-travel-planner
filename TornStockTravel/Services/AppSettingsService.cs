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
            RestockAvailabilityMode availabilityMode = NormalizeAvailabilityMode(
                storedSettings?.RestockAvailabilityMode);

            if (!string.IsNullOrWhiteSpace(storedSettings?.ProtectedTornApiKey))
            {
                return new AppSettings(Unprotect(storedSettings.ProtectedTornApiKey), bazaarPrices, buyCapacity, refreshIntervalMinutes, maxSpendPercent, availabilityMode);
            }

            return new AppSettings(storedSettings?.TornApiKey ?? string.Empty, bazaarPrices, buyCapacity, refreshIntervalMinutes, maxSpendPercent, availabilityMode);
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
            settings.RestockAvailabilityMode.ToString());
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

    private static RestockAvailabilityMode NormalizeAvailabilityMode(string? mode)
    {
        return Enum.TryParse(mode, ignoreCase: true, out RestockAvailabilityMode parsed)
            ? parsed
            : AppSettings.DefaultRestockAvailabilityMode;
    }
}

public sealed record AppSettings(
    string TornApiKey,
    IReadOnlyDictionary<int, decimal> BazaarPrices,
    int BuyCapacity,
    int RefreshIntervalMinutes,
    int MaxSpendPercent,
    RestockAvailabilityMode RestockAvailabilityMode)
{
    public const int DefaultRefreshIntervalMinutes = 5;
    public const int MinimumRefreshIntervalMinutes = 1;
    public const int MaximumRefreshIntervalMinutes = 1440;
    public const int DefaultMaxSpendPercent = 50;
    public const int MinimumMaxSpendPercent = 1;
    public const int MaximumMaxSpendPercent = 100;
    public const RestockAvailabilityMode DefaultRestockAvailabilityMode = RestockAvailabilityMode.Conservative;

    public AppSettings(string TornApiKey)
        : this(TornApiKey, new Dictionary<int, decimal>(), 0, DefaultRefreshIntervalMinutes, DefaultMaxSpendPercent, DefaultRestockAvailabilityMode)
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
    string? RestockAvailabilityMode);
