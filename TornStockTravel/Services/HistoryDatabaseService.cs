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

    public HistoryOverview BuildOverview(DateTimeOffset now)
    {
        using SqliteConnection connection = OpenConnection();

        int refreshSnapshotCount = ExecuteScalarInt(connection, "SELECT COUNT(*) FROM refresh_snapshots;");
        int itemSnapshotCount = ExecuteScalarInt(connection, "SELECT COUNT(*) FROM item_snapshots;");
        int marketSnapshotCount = ExecuteScalarInt(connection, "SELECT COUNT(*) FROM market_snapshots;");
        int restockSnapshotCount = ExecuteScalarInt(connection, "SELECT COUNT(*) FROM restock_snapshots;");
        DateTimeOffset? firstObservedAt = ExecuteScalarDateTime(connection, "SELECT observed_at_utc FROM refresh_snapshots ORDER BY observed_at_utc ASC LIMIT 1;");
        DateTimeOffset? lastObservedAt = ExecuteScalarDateTime(connection, "SELECT observed_at_utc FROM refresh_snapshots ORDER BY observed_at_utc DESC LIMIT 1;");
        long? latestRefreshId = ExecuteScalarLongNullable(connection, "SELECT id FROM refresh_snapshots ORDER BY observed_at_utc DESC LIMIT 1;");
        int latestItemCount = latestRefreshId is null
            ? 0
            : ExecuteScalarInt(connection, "SELECT item_count FROM refresh_snapshots WHERE id = $refresh_id;", ("$refresh_id", latestRefreshId.Value));

        IReadOnlyList<HistoryItemOption> itemOptions = LoadItemOptions(connection);
        IReadOnlyList<HistoryProfitOpportunity> topProfitItems = latestRefreshId is null
            ? Array.Empty<HistoryProfitOpportunity>()
            : LoadTopProfitItems(connection, latestRefreshId.Value);
        IReadOnlyList<HistoryRestockOpportunity> upcomingRestocks = latestRefreshId is null
            ? Array.Empty<HistoryRestockOpportunity>()
            : LoadUpcomingRestocks(connection, latestRefreshId.Value, now);
        IReadOnlyList<HistoryMarketMover> marketMovers = LoadMarketMovers(connection, now.AddDays(-60));

        return new HistoryOverview(
            refreshSnapshotCount,
            itemSnapshotCount,
            marketSnapshotCount,
            restockSnapshotCount,
            latestItemCount,
            firstObservedAt,
            lastObservedAt,
            itemOptions,
            topProfitItems,
            marketMovers,
            upcomingRestocks);
    }

    public HistoryItemDetail? BuildItemDetail(int itemId, string countryCode, DateTimeOffset now)
    {
        using SqliteConnection connection = OpenConnection();
        List<HistoryItemSampleRow> rows = LoadItemDetailRows(connection, itemId, countryCode, now.AddDays(-90));
        if (rows.Count == 0)
        {
            return null;
        }

        HistoryItemSampleRow first = rows.First();
        HistoryItemSampleRow latest = rows.Last();
        List<decimal> marketValues = rows
            .Where(row => row.MarketValue is not null)
            .Select(row => row.MarketValue!.Value)
            .ToList();
        List<decimal> profitPerHourValues = rows
            .Where(row => row.ProfitPerHour is not null)
            .Select(row => row.ProfitPerHour!.Value)
            .ToList();
        IReadOnlyList<HistoryItemSample> recentSamples = rows
            .OrderByDescending(row => row.ObservedAt)
            .Take(18)
            .Select(row => new HistoryItemSample(
                row.ObservedAt,
                row.Quantity,
                row.Cost,
                row.MarketValue,
                row.EffectiveValue,
                row.UnitProfit,
                row.ProfitPerHour,
                row.RestockEstimateUtc,
                row.StockoutEstimateUtc))
            .ToList();

        return new HistoryItemDetail(
            itemId,
            latest.ItemName,
            latest.CountryCode,
            latest.CountryName,
            rows.Count,
            first.ObservedAt,
            latest.ObservedAt,
            latest.Quantity,
            latest.Cost,
            latest.MarketValue,
            latest.EffectiveValue,
            latest.UnitProfit,
            latest.ProfitPerHour,
            profitPerHourValues.Count == 0 ? null : profitPerHourValues.Average(),
            marketValues.Count == 0 ? null : marketValues.Min(),
            marketValues.Count == 0 ? null : marketValues.Max(),
            rows.Count(row => row.Quantity <= 0),
            latest.RestockEstimateUtc,
            latest.StockoutEstimateUtc,
            latest.RestockConfidence,
            latest.StockoutConfidence,
            recentSamples);
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

    private static IReadOnlyList<HistoryItemOption> LoadItemOptions(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH grouped AS (
                SELECT item_id,
                       country_code,
                       COUNT(*) AS snapshot_count,
                       MAX(observed_at_utc) AS last_observed_at
                FROM item_snapshots
                GROUP BY item_id, country_code
            )
            SELECT g.item_id,
                   i.item_name,
                   g.country_code,
                   i.country_name,
                   g.snapshot_count,
                   g.last_observed_at
            FROM grouped g
            JOIN item_snapshots i
              ON i.item_id = g.item_id
             AND i.country_code = g.country_code
             AND i.observed_at_utc = g.last_observed_at
            GROUP BY g.item_id, g.country_code
            ORDER BY i.item_name COLLATE NOCASE ASC, i.country_name COLLATE NOCASE ASC
            LIMIT 500;
            """;

        List<HistoryItemOption> options = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            options.Add(new HistoryItemOption(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                ParseUtc(reader.GetString(5))));
        }

        return options;
    }

    private static List<HistoryItemSampleRow> LoadItemDetailRows(
        SqliteConnection connection,
        int itemId,
        string countryCode,
        DateTimeOffset since)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT observed_at_utc,
                   item_name,
                   country_code,
                   country_name,
                   quantity,
                   cost,
                   market_value,
                   effective_value,
                   unit_profit,
                   profit_per_hour,
                   restock_estimate_utc,
                   stockout_estimate_utc,
                   restock_confidence,
                   stockout_confidence
            FROM item_snapshots
            WHERE item_id = $item_id
              AND country_code = $country_code
              AND observed_at_utc >= $since
            ORDER BY observed_at_utc ASC
            LIMIT 1000;
            """;
        command.Parameters.AddWithValue("$item_id", itemId);
        command.Parameters.AddWithValue("$country_code", countryCode);
        command.Parameters.AddWithValue("$since", FormatUtc(since));

        List<HistoryItemSampleRow> rows = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new HistoryItemSampleRow(
                ParseUtc(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                ReadNullableDecimal(reader, 5) ?? 0,
                ReadNullableDecimal(reader, 6),
                ReadNullableDecimal(reader, 7),
                ReadNullableDecimal(reader, 8),
                ReadNullableDecimal(reader, 9),
                ReadNullableDateTimeOffset(reader, 10),
                ReadNullableDateTimeOffset(reader, 11),
                ReadNullableString(reader, 12),
                ReadNullableString(reader, 13)));
        }

        return rows;
    }

    private static IReadOnlyList<HistoryProfitOpportunity> LoadTopProfitItems(SqliteConnection connection, long refreshId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_name, country_code, country_name, profit_per_hour, profit, quantity, market_value, unit_profit
            FROM item_snapshots
            WHERE refresh_id = $refresh_id
              AND profit_per_hour IS NOT NULL
              AND profit_per_hour > 0
            ORDER BY profit_per_hour DESC
            LIMIT 8;
            """;
        command.Parameters.AddWithValue("$refresh_id", refreshId);

        List<HistoryProfitOpportunity> items = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new HistoryProfitOpportunity(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ReadNullableDecimal(reader, 3) ?? 0,
                ReadNullableDecimal(reader, 4) ?? 0,
                reader.GetInt32(5),
                ReadNullableDecimal(reader, 6),
                ReadNullableDecimal(reader, 7)));
        }

        return items;
    }

    private static IReadOnlyList<HistoryMarketMover> LoadMarketMovers(SqliteConnection connection, DateTimeOffset since)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id, item_name, observed_at_utc, market_value
            FROM market_snapshots
            WHERE market_value IS NOT NULL
              AND observed_at_utc >= $since
            ORDER BY item_id ASC, observed_at_utc ASC;
            """;
        command.Parameters.AddWithValue("$since", FormatUtc(since));

        Dictionary<int, List<(string ItemName, DateTimeOffset ObservedAt, decimal MarketValue)>> samples = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            int itemId = reader.GetInt32(0);
            if (!samples.TryGetValue(itemId, out List<(string ItemName, DateTimeOffset ObservedAt, decimal MarketValue)>? itemSamples))
            {
                itemSamples = new List<(string ItemName, DateTimeOffset ObservedAt, decimal MarketValue)>();
                samples[itemId] = itemSamples;
            }

            itemSamples.Add((
                reader.GetString(1),
                ParseUtc(reader.GetString(2)),
                ReadNullableDecimal(reader, 3) ?? 0));
        }

        return samples
            .Select(pair =>
            {
                List<(string ItemName, DateTimeOffset ObservedAt, decimal MarketValue)> ordered = pair.Value
                    .OrderBy(sample => sample.ObservedAt)
                    .ToList();
                (string ItemName, DateTimeOffset ObservedAt, decimal MarketValue) first = ordered.First();
                (string ItemName, DateTimeOffset ObservedAt, decimal MarketValue) latest = ordered.Last();

                return new HistoryMarketMover(
                    pair.Key,
                    latest.ItemName,
                    first.MarketValue,
                    latest.MarketValue,
                    first.ObservedAt,
                    latest.ObservedAt);
            })
            .Where(mover => mover.FirstMarketValue > 0 && mover.LatestObservedAt > mover.FirstObservedAt)
            .OrderByDescending(mover => Math.Abs(mover.PercentChange))
            .ThenByDescending(mover => Math.Abs(mover.AbsoluteChange))
            .Take(8)
            .ToList();
    }

    private static IReadOnlyList<HistoryRestockOpportunity> LoadUpcomingRestocks(
        SqliteConnection connection,
        long refreshId,
        DateTimeOffset now)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.item_name,
                   r.country_code,
                   COALESCE(i.country_name, r.country_code) AS country_name,
                   r.restock_estimate_utc,
                   r.stockout_estimate_utc,
                   r.restock_confidence,
                   r.stockout_confidence
            FROM restock_snapshots r
            LEFT JOIN item_snapshots i
              ON i.refresh_id = r.refresh_id
             AND i.item_id = r.item_id
             AND i.country_code = r.country_code
            WHERE r.refresh_id = $refresh_id
              AND r.restock_estimate_utc IS NOT NULL
              AND r.restock_estimate_utc >= $earliest_restock
            ORDER BY r.restock_estimate_utc ASC
            LIMIT 40;
            """;
        command.Parameters.AddWithValue("$refresh_id", refreshId);
        command.Parameters.AddWithValue("$earliest_restock", FormatUtc(now.Subtract(TimeSpan.FromMinutes(30))));

        List<HistoryRestockOpportunity> restocks = new();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string countryCode = reader.GetString(1);
            TimeSpan? flightDuration = TravelFlightTimes.GetFlightDuration(countryCode);
            if (flightDuration is null)
            {
                continue;
            }

            DateTimeOffset restockAt = ParseUtc(reader.GetString(3));
            DateTimeOffset? stockoutAt = ReadNullableDateTimeOffset(reader, 4);
            DateTimeOffset latestArrival = restockAt.AddMinutes(30);
            if (stockoutAt is not null && stockoutAt.Value < latestArrival)
            {
                latestArrival = stockoutAt.Value;
            }

            DateTimeOffset latestDeparture = latestArrival.Subtract(flightDuration.Value);
            if (now > latestDeparture)
            {
                continue;
            }

            DateTimeOffset targetDeparture = restockAt.Subtract(flightDuration.Value);
            DateTimeOffset suggestedDeparture = now > targetDeparture ? now : targetDeparture;
            DateTimeOffset expectedArrival = suggestedDeparture.Add(flightDuration.Value);
            if (expectedArrival > latestArrival)
            {
                continue;
            }

            restocks.Add(new HistoryRestockOpportunity(
                reader.GetString(0),
                countryCode,
                reader.GetString(2),
                restockAt,
                stockoutAt,
                ReadNullableString(reader, 5),
                ReadNullableString(reader, 6),
                flightDuration.Value,
                suggestedDeparture,
                latestDeparture,
                expectedArrival));

            if (restocks.Count >= 8)
            {
                break;
            }
        }

        return restocks;
    }

    private static decimal? ReadNullableDecimal(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : Convert.ToDecimal(reader.GetDouble(ordinal), CultureInfo.InvariantCulture);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : reader.GetString(ordinal);
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? null
            : ParseUtc(reader.GetString(ordinal));
    }

    private static int ExecuteScalarInt(SqliteConnection connection, string commandText, params (string Name, object Value)[] parameters)
    {
        object? value = ExecuteScalar(connection, commandText, parameters);
        return value is null or DBNull
            ? 0
            : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static long? ExecuteScalarLongNullable(SqliteConnection connection, string commandText, params (string Name, object Value)[] parameters)
    {
        object? value = ExecuteScalar(connection, commandText, parameters);
        return value is null or DBNull
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ExecuteScalarDateTime(SqliteConnection connection, string commandText, params (string Name, object Value)[] parameters)
    {
        object? value = ExecuteScalar(connection, commandText, parameters);
        return value is string text && !string.IsNullOrWhiteSpace(text)
            ? ParseUtc(text)
            : null;
    }

    private static object? ExecuteScalar(SqliteConnection connection, string commandText, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteScalar();
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
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

    private sealed record HistoryItemSampleRow(
        DateTimeOffset ObservedAt,
        string ItemName,
        string CountryCode,
        string CountryName,
        int Quantity,
        decimal Cost,
        decimal? MarketValue,
        decimal? EffectiveValue,
        decimal? UnitProfit,
        decimal? ProfitPerHour,
        DateTimeOffset? RestockEstimateUtc,
        DateTimeOffset? StockoutEstimateUtc,
        string? RestockConfidence,
        string? StockoutConfidence);
}
