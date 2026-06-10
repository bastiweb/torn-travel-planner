using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace TornStockTravel.Services;

public sealed class TornMoneyClient : IDisposable
{
    private static readonly Uri MoneyUri = new("https://api.torn.com/v2/user/money");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;

    public TornMoneyClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = RequestTimeout
        };
    }

    public async Task<TornMoneyStatus?> FetchMoneyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        using HttpRequestMessage request = new(HttpMethod.Get, MoneyUri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", apiKey.Trim());
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Torn money API returned HTTP {(int)response.StatusCode}.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("error", out JsonElement errorElement))
        {
            string code = errorElement.TryGetProperty("code", out JsonElement codeElement)
                ? codeElement.ToString()
                : "unknown";
            string message = errorElement.TryGetProperty("error", out JsonElement messageElement)
                ? messageElement.ToString()
                : "Unknown Torn money API error";

            throw new InvalidOperationException($"Torn money API error {code}: {message}");
        }

        JsonElement moneyElement = root.TryGetProperty("money", out JsonElement nestedMoney)
            ? nestedMoney
            : root;

        decimal? wallet = ReadNullableDecimal(moneyElement, "wallet");
        decimal vault = ReadNullableDecimal(moneyElement, "vault")
            ?? ReadNestedNullableDecimal(moneyElement, "vault", "amount")
            ?? ReadNestedNullableDecimal(moneyElement, "vault", "money")
            ?? 0;

        return wallet is null ? null : new TornMoneyStatus(wallet.Value, vault);
    }

    private static decimal? ReadNestedNullableDecimal(JsonElement element, string parentPropertyName, string childPropertyName)
    {
        if (!element.TryGetProperty(parentPropertyName, out JsonElement parent)
            || parent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ReadNullableDecimal(parent, childPropertyName);
    }

    private static decimal? ReadNullableDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null
            || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number))
        {
            return number;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal invariantParsed)
            ? invariantParsed
            : decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal currentCultureParsed)
                ? currentCultureParsed
                : null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
