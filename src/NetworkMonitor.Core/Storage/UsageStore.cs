using Microsoft.Data.Sqlite;

namespace NetworkMonitor.Core.Storage;

/// <summary>
/// Minute-granularity traffic ledger. One row per (minute, interface), rows are small and are kept forever
/// so "总计" is real rather than "since the app last started".
/// </summary>
public sealed class UsageStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private bool _disposed;

    public UsageStore(string databasePath)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };

        _conn = new SqliteConnection(builder.ToString());
        _conn.Open();

        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA busy_timeout=4000;");
        Exec("""
             CREATE TABLE IF NOT EXISTS minute_usage(
                 bucket INTEGER NOT NULL,          -- unix minute (UTC)
                 iface  TEXT    NOT NULL,          -- "luid:xxxxxxxxxxxxxxxx"
                 rx     INTEGER NOT NULL,          -- bytes received in that minute
                 tx     INTEGER NOT NULL,
                 PRIMARY KEY (bucket, iface)
             ) WITHOUT ROWID;
             """);
        Exec("CREATE INDEX IF NOT EXISTS ix_minute_usage_iface ON minute_usage(iface, bucket);");
        Exec("CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);");
    }

    public static long BucketOf(DateTimeOffset instant) => instant.ToUnixTimeSeconds() / 60;

    public void AddInterval(long bucket, string interfaceKey, long rx, long tx)
    {
        if (rx <= 0 && tx <= 0) return;

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO minute_usage(bucket, iface, rx, tx) VALUES ($b, $i, $rx, $tx)
                ON CONFLICT(bucket, iface) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx;
                """;
            cmd.Parameters.AddWithValue("$b", bucket);
            cmd.Parameters.AddWithValue("$i", interfaceKey);
            cmd.Parameters.AddWithValue("$rx", rx);
            cmd.Parameters.AddWithValue("$tx", tx);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddIntervals(IEnumerable<(long Bucket, string InterfaceKey, long Rx, long Tx)> rows)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO minute_usage(bucket, iface, rx, tx) VALUES ($b, $i, $rx, $tx)
                ON CONFLICT(bucket, iface) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx;
                """;
            var pb = cmd.Parameters.Add("$b", SqliteType.Integer);
            var pi = cmd.Parameters.Add("$i", SqliteType.Text);
            var prx = cmd.Parameters.Add("$rx", SqliteType.Integer);
            var ptx = cmd.Parameters.Add("$tx", SqliteType.Integer);

            foreach (var row in rows)
            {
                pb.Value = row.Bucket;
                pi.Value = row.InterfaceKey;
                prx.Value = row.Rx;
                ptx.Value = row.Tx;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public UsageAmount SumRange(long fromBucketInclusive, long toBucketExclusive, IReadOnlyCollection<string>? interfaces)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            string filter = BuildInterfaceFilter(cmd, interfaces);
            cmd.CommandText = $"""
                SELECT COALESCE(SUM(rx), 0), COALESCE(SUM(tx), 0)
                FROM minute_usage
                WHERE bucket >= $from AND bucket < $to{filter};
                """;
            cmd.Parameters.AddWithValue("$from", fromBucketInclusive);
            cmd.Parameters.AddWithValue("$to", toBucketExclusive);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new UsageAmount(reader.GetInt64(0), reader.GetInt64(1)) : UsageAmount.Zero;
        }
    }

    public UsageAmount SumAll(IReadOnlyCollection<string>? interfaces)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            string filter = BuildInterfaceFilter(cmd, interfaces);
            cmd.CommandText = $"SELECT COALESCE(SUM(rx), 0), COALESCE(SUM(tx), 0) FROM minute_usage WHERE 1=1{filter};";
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new UsageAmount(reader.GetInt64(0), reader.GetInt64(1)) : UsageAmount.Zero;
        }
    }

    /// <summary>Raw rows for the range, aggregated per local calendar day.</summary>
    public IReadOnlyList<DailyUsage> DailyLocal(
        long fromBucketInclusive,
        long toBucketExclusive,
        TimeZoneInfo timeZone,
        IReadOnlyCollection<string>? interfaces)
    {
        var byDay = new Dictionary<DateOnly, (long Rx, long Tx)>();

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            string filter = BuildInterfaceFilter(cmd, interfaces);
            cmd.CommandText = $"""
                SELECT bucket, COALESCE(SUM(rx), 0), COALESCE(SUM(tx), 0)
                FROM minute_usage
                WHERE bucket >= $from AND bucket < $to{filter}
                GROUP BY bucket;
                """;
            cmd.Parameters.AddWithValue("$from", fromBucketInclusive);
            cmd.Parameters.AddWithValue("$to", toBucketExclusive);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long bucket = reader.GetInt64(0);
                long rx = reader.GetInt64(1);
                long tx = reader.GetInt64(2);

                var utc = DateTimeOffset.FromUnixTimeSeconds(bucket * 60).UtcDateTime;
                var local = TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
                var day = DateOnly.FromDateTime(local);

                var current = byDay.TryGetValue(day, out var acc) ? acc : (0L, 0L);
                byDay[day] = (current.Item1 + rx, current.Item2 + tx);
            }
        }

        return byDay
            .OrderBy(kv => kv.Key)
            .Select(kv => new DailyUsage(kv.Key, kv.Value.Rx, kv.Value.Tx))
            .ToList();
    }

    public DateOnly? FirstTrackedLocal(TimeZoneInfo timeZone)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT MIN(bucket) FROM minute_usage;";
            object? value = cmd.ExecuteScalar();
            if (value is null or DBNull) return null;

            long bucket = Convert.ToInt64(value);
            var utc = DateTimeOffset.FromUnixTimeSeconds(bucket * 60).UtcDateTime;
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone));
        }
    }

    private static string BuildInterfaceFilter(SqliteCommand cmd, IReadOnlyCollection<string>? interfaces)
    {
        if (interfaces is null || interfaces.Count == 0) return string.Empty;

        var names = new List<string>(interfaces.Count);
        int i = 0;
        foreach (string key in interfaces)
        {
            string name = "$k" + i++;
            names.Add(name);
            cmd.Parameters.AddWithValue(name, key);
        }

        return " AND iface IN (" + string.Join(",", names) + ")";
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try
            {
                Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch
            {
                // best effort
            }

            _conn.Dispose();
        }
    }
}
