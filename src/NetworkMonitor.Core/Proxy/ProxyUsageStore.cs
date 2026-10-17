using Microsoft.Data.Sqlite;

namespace NetworkMonitor.Core.Proxy;

/// <summary>
/// Minute ledger for traffic that passed through the proxy, keyed by process + proxy node.
/// Kept separate from the NIC ledger so proxy and physical totals are never mixed.
/// </summary>
public sealed class ProxyUsageStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();
    private bool _disposed;

    public ProxyUsageStore(string databasePath)
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
             CREATE TABLE IF NOT EXISTS proxy_usage(
                 bucket  INTEGER NOT NULL,
                 process TEXT    NOT NULL,
                 node    TEXT    NOT NULL,
                 rx      INTEGER NOT NULL,
                 tx      INTEGER NOT NULL,
                 PRIMARY KEY (bucket, process, node)
             ) WITHOUT ROWID;
             """);
        Exec("CREATE INDEX IF NOT EXISTS ix_proxy_usage_bucket ON proxy_usage(bucket);");
    }

    public void AddIntervals(IEnumerable<(long Bucket, string Process, string Node, long Rx, long Tx)> rows)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO proxy_usage(bucket, process, node, rx, tx) VALUES ($b, $p, $n, $rx, $tx)
                ON CONFLICT(bucket, process, node) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx;
                """;
            var pb = cmd.Parameters.Add("$b", SqliteType.Integer);
            var pp = cmd.Parameters.Add("$p", SqliteType.Text);
            var pn = cmd.Parameters.Add("$n", SqliteType.Text);
            var prx = cmd.Parameters.Add("$rx", SqliteType.Integer);
            var ptx = cmd.Parameters.Add("$tx", SqliteType.Integer);

            foreach (var row in rows)
            {
                pb.Value = row.Bucket;
                pp.Value = row.Process;
                pn.Value = row.Node;
                prx.Value = row.Rx;
                ptx.Value = row.Tx;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public IReadOnlyList<(string Process, string Node, long Rx, long Tx)> QueryGrouped(
        long fromBucketInclusive, long toBucketExclusive)
    {
        var rows = new List<(string Process, string Node, long Rx, long Tx)>();

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT process, node, COALESCE(SUM(rx), 0), COALESCE(SUM(tx), 0)
                FROM proxy_usage
                WHERE bucket >= $from AND bucket < $to
                GROUP BY process, node;
                """;
            cmd.Parameters.AddWithValue("$from", fromBucketInclusive);
            cmd.Parameters.AddWithValue("$to", toBucketExclusive);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
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
