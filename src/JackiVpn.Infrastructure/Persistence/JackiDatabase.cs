using Microsoft.Data.Sqlite;

namespace JackiVpn.Infrastructure.Persistence;

/// <summary>
/// SQLite database (WAL, foreign keys). Secrets are never stored in clear text: subscription URLs and full
/// profile payloads (credentials, raw source) are DPAPI-encrypted blobs; only non-secret columns needed for
/// listing/searching are stored in the clear.
/// </summary>
public sealed class JackiDatabase
{
    public const int SchemaVersion = 1;
    private readonly string _connectionString;

    public JackiDatabase(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        _path = path;
        if (File.Exists(path) && !IsHealthy())
        {
            Recover();
        }

        Migrate();
    }

    private readonly string _path;

    /// <summary>Set when the database was damaged and rebuilt at start (path of the kept damaged copy).</summary>
    public string? RecoveredFrom { get; private set; }

    /// <summary>Rows that could not be read back during recovery, per table.</summary>
    public IReadOnlyDictionary<string, int> RecoveryLosses { get; private set; } = new Dictionary<string, int>();

    private bool IsHealthy()
    {
        try
        {
            using var c = new SqliteConnection(_connectionString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check(1)";
            return string.Equals(cmd.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// A damaged database (power loss, disk error) must not keep the app from starting. The damaged file is kept
    /// next to it (<c>.damaged-…</c>), a fresh database is created, and every row that can still be read is copied
    /// over table by table (full scans, so a broken index does not hide rows). Health samples are only a cache and
    /// are not carried over.
    /// </summary>
    private void Recover()
    {
        SqliteConnection.ClearAllPools();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var damaged = _path + ".damaged-" + stamp;
        File.Move(_path, damaged);
        foreach (var side in new[] { "-wal", "-shm" })
        {
            if (File.Exists(_path + side))
            {
                File.Move(_path + side, damaged + side);
            }
        }

        RecoveredFrom = damaged;
        Migrate();
        var losses = new Dictionary<string, int>(StringComparer.Ordinal);
        using var target = new SqliteConnection(_connectionString);
        target.Open();
        using (var off = target.CreateCommand())
        {
            off.CommandText = "PRAGMA foreign_keys = OFF;";
            off.ExecuteNonQuery();
        }

        using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = damaged, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            source.Open();
        }
        catch (SqliteException)
        {
            return;
        }

        foreach (var table in new[] { "settings", "subscriptions", "profiles", "mux_benchmarks", "connection_history" })
        {
            var copied = 0;
            var failed = 0;
            try
            {
                using var read = source.CreateCommand();
                read.CommandText = $"SELECT * FROM {table} NOT INDEXED";
                using var reader = read.ExecuteReader();
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                using var tx = target.BeginTransaction();
                using var insert = target.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = $"INSERT OR IGNORE INTO {table} ({string.Join(',', columns)}) VALUES ({string.Join(',', columns.Select((_, i) => "$p" + i))})";
                var parameters = columns.Select((_, i) => insert.Parameters.Add(new SqliteParameter("$p" + i, null))).ToArray();
                while (true)
                {
                    try
                    {
                        if (!reader.Read())
                        {
                            break;
                        }
                    }
                    catch (SqliteException)
                    {
                        failed++;
                        break; // the rest of this table sits on unreadable pages
                    }

                    for (var i = 0; i < columns.Length; i++)
                    {
                        parameters[i].Value = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                    }

                    try
                    {
                        insert.ExecuteNonQuery();
                        copied++;
                    }
                    catch (SqliteException)
                    {
                        failed++;
                    }
                }

                tx.Commit();
            }
            catch (SqliteException)
            {
                failed++;
            }

            if (failed > 0)
            {
                losses[table] = failed;
            }
        }

        RecoveryLosses = losses;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var c = new SqliteConnection(_connectionString);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return c;
    }

    private void Migrate()
    {
        using var c = new SqliteConnection(_connectionString);
        c.Open();
        using (var pragma = c.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }

        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS subscriptions (
                id TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                url_protected BLOB NOT NULL,
                created_utc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS profiles (
                id TEXT PRIMARY KEY,
                subscription_id TEXT NULL REFERENCES subscriptions(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                protocol TEXT NOT NULL,
                server TEXT NOT NULL,
                port INTEGER NOT NULL,
                fingerprint TEXT NOT NULL,
                sync_state TEXT NOT NULL,
                is_favorite INTEGER NOT NULL DEFAULT 0,
                sort_order INTEGER NOT NULL DEFAULT 0,
                payload_protected BLOB NOT NULL,
                updated_utc INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_profiles_subscription ON profiles(subscription_id);
            CREATE INDEX IF NOT EXISTS ix_profiles_fingerprint ON profiles(fingerprint);
            CREATE TABLE IF NOT EXISTS health_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                profile_id TEXT NOT NULL,
                ts INTEGER NOT NULL,
                json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_health_profile_ts ON health_samples(profile_id, ts DESC);
            CREATE TABLE IF NOT EXISTS mux_benchmarks (
                fingerprint TEXT PRIMARY KEY,
                json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS connection_history (
                id TEXT PRIMARY KEY,
                profile_id TEXT NOT NULL,
                started_utc INTEGER NOT NULL,
                outcome TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_history_profile ON connection_history(profile_id, started_utc DESC);
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                json TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        using var ver = c.CreateCommand();
        ver.Transaction = tx;
        ver.CommandText = "SELECT COUNT(*) FROM schema_info";
        if (Convert.ToInt64(ver.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            ver.CommandText = $"INSERT INTO schema_info(version) VALUES ({SchemaVersion})";
            ver.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
