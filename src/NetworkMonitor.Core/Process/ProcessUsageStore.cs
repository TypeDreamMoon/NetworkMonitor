using Microsoft.Data.Sqlite;

namespace NetworkMonitor.Core.Process;

/// <summary>
/// Minute ledger for system-wide traffic attributed to a process. Kept apart from the NIC
/// ledger and the proxy ledger so the three views can never be mixed up.
/// </summary>
public sealed class ProcessUsageStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private bool _disposed;

    public ProcessUsageStore(string databasePath)
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
             CREATE TABLE IF NOT EXISTS process_usage(
                 bucket  INTEGER NOT NULL,
                 process TEXT    NOT NULL,
                 rx      INTEGER NOT NULL,
                 tx      INTEGER NOT NULL,
                 PRIMARY KEY (bucket, process)
             ) WITHOUT ROWID;
             """);
        Exec("CREATE INDEX IF NOT EXISTS ix_process_usage_bucket ON process_usage(bucket);");
    }

    public void AddIntervals(IEnumerable<(long Bucket, string Process, long Rx, long Tx)> rows)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO process_usage(bucket, process, rx, tx) VALUES ($b, $p, $rx, $tx)
                ON CONFLICT(bucket, process) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx;
                """;
            var pb = cmd.Parameters.Add("$b", SqliteType.Integer);
            var pp = cmd.Parameters.Add("$p", SqliteType.Text);
            var prx = cmd.Parameters.Add("$rx", SqliteType.Integer);
            var ptx = cmd.Parameters.Add("$tx", SqliteType.Integer);

            foreach (var row in rows)
            {
                pb.Value = row.Bucket;
                pp.Value = row.Process;
                prx.Value = row.Rx;
                ptx.Value = row.Tx;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<(string Process, long Rx, long Tx)> QueryGrouped(long fromBucketInclusive, long toBucketExclusive)
    {
        var rows = new List<(string Process, long Rx, long Tx)>();

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT process, COALESCE(SUM(rx), 0), COALESCE(SUM(tx), 0)
                FROM process_usage
                WHERE bucket >= $from AND bucket < $to
                GROUP BY process;
                """;
            cmd.Parameters.AddWithValue("$from", fromBucketInclusive);
            cmd.Parameters.AddWithValue("$to", toBucketExclusive);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
            }
        }

        return rows;
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
