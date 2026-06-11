using System.IO;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TornStockTravel.Services;

public sealed class HistoryDatabaseService
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;
    private readonly string _connectionString;

    public HistoryDatabaseService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string appDirectory = Path.Combine(appData, "TornStockTravel");
        Directory.CreateDirectory(appDirectory);
        _databasePath = Path.Combine(appDirectory, "torn-stock-history.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        Initialize();
    }

    public string DatabasePath => _databasePath;

    public void SaveRefreshSnapshot(
        IReadOnlyList<TravelDestination> destinations,
        DateTimeOffset observedAt,
        string source = "live-refresh")
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();

        int itemCount = destinations.Sum(destination => destination.Items.Count(item => item.Id > 0));
        long refreshId = InsertRefreshSnapshot(connection, transaction, observedAt, source, itemCount);

        using SqliteCommand itemCommand = CreateItemSnapshotCommand(connection, transaction);
        using SqliteCommand marketCommand = CreateMarketSnapshotCommand(connection, transaction);
        using SqliteCommand restockCommand = CreateRestockSnapshotCommand(connection, transaction);

        foreach (TravelDestination destination in destinations)
        {
            foreach (TravelItem item in destination.Items)
            {
                if (item.Id <= 0)
                {
                    continue;
                }

                InsertItemSnapshot(itemCommand, refreshId, observedAt, destination, item, source);
                InsertMarketSnapshot(marketCommand, refreshId, observedAt, item);

                if (item.RestockInfo is not null)
                {
                    InsertRestockSnapshot(restockCommand, refreshId, observedAt, destination, item);
                }
            }
        }

        InsertAppEvent(
            connection,
            transaction,
            observedAt,
            "refresh_snapshot_saved",
            $"Saved {itemCount:N0} history item snapshots.");

        transaction.Commit();
    }

    public IReadOnlyDictionary<string, HistoryTrendInfo> BuildTrendLookup(
        IReadOnlyList<TravelDestination> destinations,
        DateTimeOffset now)
    {
        Dictionary<string, HistoryTrendInfo> trends = new(StringComparer.OrdinalIgnoreCase);
        List<(int ItemId, string CountryCode)> keys = destinations
            .SelectMany(destination => destination.Items
                .Where(item => item.Id > 0)
                .Select(item => (item.Id, destination.Code)))
            .Distinct()
            .ToList();

        if (keys.Count == 0)
        {
            return trends;
        }

        using SqliteConnection connection = OpenConnection();
        foreach ((int itemId, string countryCode) in keys)
        {
            List<HistorySample> samples = LoadSamples(connection, itemId, countryCode, now.AddDays(-60));
            if (samples.Count == 0)
            {
                continue;
            }

            trends[BuildTrendKey(itemId, countryCode)] = TrendAnalysisService.Analyze(samples);
        }

        return trends;
    }

    public static string BuildTrendKey(int itemId, string countryCode)
    {
        return $"{countryCode.ToLowerInvariant()}:{itemId}";
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS refresh_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observed_at_utc TEXT NOT NULL,
                source TEXT NOT NULL,
                item_count INTEGER NOT NULL
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS item_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                refresh_id INTEGER NOT NULL,
                observed_at_utc TEXT NOT NULL,
                source TEXT NOT NULL,
                item_id INTEGER NOT NULL,
                item_name TEXT NOT NULL,
                country_code TEXT NOT NULL,
                country_name TEXT NOT NULL,
                quantity INTEGER NOT NULL,
                cost REAL NOT NULL,
                market_value REAL NULL,
                bazaar_price REAL NULL,
                effective_value REAL NULL,
                unit_profit REAL NULL,
                profit REAL NULL,
                profit_per_hour REAL NULL,
                owned_amount INTEGER NOT NULL,
                owned_value REAL NULL,
                buy_amount INTEGER NOT NULL,
                cash_needed REAL NOT NULL,
                restock_estimate_utc TEXT NULL,
                stockout_estimate_utc TEXT NULL,
                restock_confidence TEXT NULL,
                stockout_confidence TEXT NULL,
                restock_availability TEXT NULL,
                FOREIGN KEY(refresh_id) REFERENCES refresh_snapshots(id)
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS market_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                refresh_id INTEGER NOT NULL,
                observed_at_utc TEXT NOT NULL,
                item_id INTEGER NOT NULL,
                item_name TEXT NOT NULL,
                market_value REAL NULL,
                bazaar_price REAL NULL,
                effective_value REAL NULL,
                FOREIGN KEY(refresh_id) REFERENCES refresh_snapshots(id)
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS restock_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                refresh_id INTEGER NOT NULL,
                observed_at_utc TEXT NOT NULL,
                item_id INTEGER NOT NULL,
                item_name TEXT NOT NULL,
                country_code TEXT NOT NULL,
                restock_estimate_utc TEXT NULL,
                stockout_estimate_utc TEXT NULL,
                restock_confidence TEXT NULL,
                stockout_confidence TEXT NULL,
                restock_availability TEXT NULL,
                FOREIGN KEY(refresh_id) REFERENCES refresh_snapshots(id)
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS app_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observed_at_utc TEXT NOT NULL,
                event_type TEXT NOT NULL,
                message TEXT NOT NULL
            );
            """);

        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_item_snapshots_item_country_time ON item_snapshots(item_id, country_code, observed_at_utc);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_market_snapshots_item_time ON market_snapshots(item_id, observed_at_utc);");
        ExecuteNonQuery(connection, "CREATE INDEX IF NOT EXISTS idx_restock_snapshots_item_country_time ON restock_snapshots(item_id, country_code, observed_at_utc);");
        ExecuteNonQuery(connection, $"PRAGMA user_version = {SchemaVersion};");
    }

    private SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static long InsertRefreshSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset observedAt,
        string source,
        int itemCount)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO refresh_snapshots (observed_at_utc, source, item_count)
            VALUES ($observed_at_utc, $source, $item_count);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$observed_at_utc", FormatUtc(observedAt));
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$item_count", itemCount);
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteCommand CreateItemSnapshotCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO item_snapshots (
                refresh_id, observed_at_utc, source, item_id, item_name, country_code, country_name,
                quantity, cost, market_value, bazaar_price, effective_value, unit_profit, profit,
                profit_per_hour, owned_amount, owned_value, buy_amount, cash_needed,
                restock_estimate_utc, stockout_estimate_utc, restock_confidence,
                stockout_confidence, restock_availability)
            VALUES (
                $refresh_id, $observed_at_utc, $source, $item_id, $item_name, $country_code, $country_name,
                $quantity, $cost, $market_value, $bazaar_price, $effective_value, $unit_profit, $profit,
                $profit_per_hour, $owned_amount, $owned_value, $buy_amount, $cash_needed,
                $restock_estimate_utc, $stockout_estimate_utc, $restock_confidence,
                $stockout_confidence, $restock_availability);
            """;
        AddParameters(command,
            "$refresh_id", "$observed_at_utc", "$source", "$item_id", "$item_name", "$country_code", "$country_name",
            "$quantity", "$cost", "$market_value", "$bazaar_price", "$effective_value", "$unit_profit", "$profit",
            "$profit_per_hour", "$owned_amount", "$owned_value", "$buy_amount", "$cash_needed",
            "$restock_estimate_utc", "$stockout_estimate_utc", "$restock_confidence",
            "$stockout_confidence", "$restock_availability");
        return command;
    }

    private static SqliteCommand CreateMarketSnapshotCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO market_snapshots (
                refresh_id, observed_at_utc, item_id, item_name, market_value, bazaar_price, effective_value)
            VALUES (
                $refresh_id, $observed_at_utc, $item_id, $item_name, $market_value, $bazaar_price, $effective_value);
            """;
        AddParameters(command, "$refresh_id", "$observed_at_utc", "$item_id", "$item_name", "$market_value", "$bazaar_price", "$effective_value");
        return command;
    }

    private static SqliteCommand CreateRestockSnapshotCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO restock_snapshots (
                refresh_id, observed_at_utc, item_id, item_name, country_code,
                restock_estimate_utc, stockout_estimate_utc, restock_confidence,
                stockout_confidence, restock_availability)
            VALUES (
                $refresh_id, $observed_at_utc, $item_id, $item_name, $country_code,
                $restock_estimate_utc, $stockout_estimate_utc, $restock_confidence,
                $stockout_confidence, $restock_availability);
            """;
        AddParameters(command,
            "$refresh_id", "$observed_at_utc", "$item_id", "$item_name", "$country_code",
            "$restock_estimate_utc", "$stockout_estimate_utc", "$restock_confidence",
            "$stockout_confidence", "$restock_availability");
        return command;
    }

    private static void InsertItemSnapshot(
        SqliteCommand command,
        long refreshId,
        DateTimeOffset observedAt,
        TravelDestination destination,
        TravelItem item,
        string source)
    {
        SetValue(command, "$refresh_id", refreshId);
        SetValue(command, "$observed_at_utc", FormatUtc(observedAt));
        SetValue(command, "$source", source);
        SetValue(command, "$item_id", item.Id);
        SetValue(command, "$item_name", item.Name);
        SetValue(command, "$country_code", destination.Code);
        SetValue(command, "$country_name", destination.Name);
        SetValue(command, "$quantity", item.Quantity);
        SetValue(command, "$cost", item.Cost);
        SetValue(command, "$market_value", item.MarketValue);
        SetValue(command, "$bazaar_price", item.BazaarPrice);
        SetValue(command, "$effective_value", item.EffectiveValue);
        SetValue(command, "$unit_profit", item.UnitProfit);
        SetValue(command, "$profit", item.Profit);
        SetValue(command, "$profit_per_hour", item.ProfitPerHour);
        SetValue(command, "$owned_amount", item.OwnedAmount);
        SetValue(command, "$owned_value", item.OwnedValue);
        SetValue(command, "$buy_amount", item.BuyAmount);
        SetValue(command, "$cash_needed", item.CashNeeded);
        SetValue(command, "$restock_estimate_utc", FormatUtc(item.RestockEstimateUtc));
        SetValue(command, "$stockout_estimate_utc", FormatUtc(item.StockoutEstimateUtc));
        SetValue(command, "$restock_confidence", item.RestockInfo?.Confidence);
        SetValue(command, "$stockout_confidence", item.RestockInfo?.StockoutConfidenceText);
        SetValue(command, "$restock_availability", item.RestockAvailabilityText);
        command.ExecuteNonQuery();
    }

    private static void InsertMarketSnapshot(
        SqliteCommand command,
        long refreshId,
        DateTimeOffset observedAt,
        TravelItem item)
    {
        if (item.Id <= 0 || item.MarketValue is null && item.BazaarPrice is null)
        {
            return;
        }

        SetValue(command, "$refresh_id", refreshId);
        SetValue(command, "$observed_at_utc", FormatUtc(observedAt));
        SetValue(command, "$item_id", item.Id);
        SetValue(command, "$item_name", item.Name);
        SetValue(command, "$market_value", item.MarketValue);
        SetValue(command, "$bazaar_price", item.BazaarPrice);
        SetValue(command, "$effective_value", item.EffectiveValue);
        command.ExecuteNonQuery();
    }

    private static void InsertRestockSnapshot(
        SqliteCommand command,
        long refreshId,
        DateTimeOffset observedAt,
        TravelDestination destination,
        TravelItem item)
    {
        SetValue(command, "$refresh_id", refreshId);
        SetValue(command, "$observed_at_utc", FormatUtc(observedAt));
        SetValue(command, "$item_id", item.Id);
        SetValue(command, "$item_name", item.Name);
        SetValue(command, "$country_code", destination.Code);
        SetValue(command, "$restock_estimate_utc", FormatUtc(item.RestockEstimateUtc));
        SetValue(command, "$stockout_estimate_utc", FormatUtc(item.StockoutEstimateUtc));
        SetValue(command, "$restock_confidence", item.RestockInfo?.Confidence);
        SetValue(command, "$stockout_confidence", item.RestockInfo?.StockoutConfidenceText);
        SetValue(command, "$restock_availability", item.RestockAvailabilityText);
        command.ExecuteNonQuery();
    }

    private static void InsertAppEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset observedAt,
        string eventType,
        string message)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_events (observed_at_utc, event_type, message)
            VALUES ($observed_at_utc, $event_type, $message);
            """;
        command.Parameters.AddWithValue("$observed_at_utc", FormatUtc(observedAt));
        command.Parameters.AddWithValue("$event_type", eventType);
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    private static List<HistorySample> LoadSamples(
        SqliteConnection connection,
        int itemId,
        string countryCode,
        DateTimeOffset since)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT observed_at_utc, quantity, market_value, unit_profit, profit_per_hour
            FROM item_snapshots
            WHERE item_id = $item_id
              AND country_code = $country_code
              AND observed_at_utc >= $since
            ORDER BY observed_at_utc ASC;
            """;
        command.Parameters.AddWithValue("$item_id", itemId);
        command.Parameters.AddWithValue("$country_code", countryCode);
        command.Parameters.AddWithValue("$since", FormatUtc(since));

        List<HistorySample> samples = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new HistorySample(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                reader.GetInt32(1),
                ReadNullableDecimal(reader, 2),
                ReadNullableDecimal(reader, 3),
                ReadNullableDecimal(reader, 4)));
        }

        return samples;
    }

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDecimal(reader.GetDouble(ordinal), CultureInfo.InvariantCulture);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand command, params string[] names)
    {
        foreach (string name in names)
        {
            command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        }
    }

    private static void SetValue(SqliteCommand command, string name, object? value)
    {
        command.Parameters[name].Value = value switch
        {
            null => DBNull.Value,
            decimal decimalValue => Convert.ToDouble(decimalValue, CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static string? FormatUtc(DateTimeOffset? value)
    {
        return value is null ? null : FormatUtc(value.Value);
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
