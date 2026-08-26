using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WasabiDrive.Core.Sync;

/// <summary>
/// Thread-safe, persisted map of S3 key → last-synced <see cref="SyncEntry"/> for one mapping,
/// stored as SQLite under <c>%LOCALAPPDATA%\WasabiDrive\sync\&lt;mappingId&gt;.db</c>. Lets the
/// reconciler tell local-only vs remote-only vs both-changed.
///
/// This used to be a JSON document that was re-serialised in full on every single
/// <see cref="Set"/>. That is O(n) work per key and O(n²) over a population pass, so a bucket
/// with a few hundred thousand objects never finished writing its own state file — and the whole
/// map had to sit in memory to be serialised at all. SQLite makes a write O(log n), bounds memory
/// to the rows actually being read, and lets the callers that only care about one folder ask for
/// one folder (<see cref="EntriesWithPrefix"/>) instead of pulling every key.
/// </summary>
public sealed class SyncStateStore : IDisposable
{
    /// <summary>
    /// SqliteConnection is not safe for concurrent commands, and the callers here are a mix of
    /// FileSystemWatcher threads, hydration tasks and the reconcile timer. One lock around every
    /// statement is plenty: these are microsecond-scale indexed writes on a local file.
    /// </summary>
    private readonly object _gate = new();

    private readonly SqliteConnection _db;
    private bool _disposed;

    public SyncStateStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A state file path is required.", nameof(filePath));

        var dbPath = Path.ChangeExtension(filePath, ".db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        _db.Open();

        Execute(
            // WAL keeps the frequent small writes off the main database file, and NORMAL means we
            // don't fsync on every commit. The worst case for either is losing the last few
            // entries after a hard power cut, which the next reconcile re-derives from the bucket.
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA synchronous=NORMAL;" +
            // Keys are the primary key and every query is by key or key range, so the rowid
            // indirection is pure overhead.
            "CREATE TABLE IF NOT EXISTS entries (" +
            "  key TEXT PRIMARY KEY," +
            "  etag TEXT," +
            "  size INTEGER NOT NULL," +
            "  remote_ticks INTEGER NOT NULL," +
            "  local_ticks INTEGER NOT NULL" +
            ") WITHOUT ROWID;" +
            // Which directories have been listed at least once. The obvious way to find these is
            // to walk the folder on disk — but enumerating an unpopulated directory placeholder is
            // precisely what makes Windows ask us to fill it in, so a recursive walk would populate
            // the entire bucket and undo the laziness it was trying to inspect. Recording them here
            // keeps every background pass off the file system.
            "CREATE TABLE IF NOT EXISTS directories (" +
            "  prefix TEXT PRIMARY KEY," +
            "  listed_ticks INTEGER NOT NULL" +
            ") WITHOUT ROWID;");

        MigrateLegacyJson(Path.ChangeExtension(dbPath, ".json"));
    }

    /// <summary>Default per-mapping state database under the app data folder.</summary>
    public static string FilePathFor(Guid mappingId) =>
        Path.Combine(AppPaths.BaseDir, "sync", mappingId.ToString("N") + ".db");

    public SyncEntry? Get(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT key, etag, size, remote_ticks, local_ticks FROM entries WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    public void Set(SyncEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = UpsertSql;
            Bind(cmd, entry);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records many entries in one transaction. Populating a directory writes its whole listing at
    /// once; committing per row would fsync once per object for no benefit.
    /// </summary>
    public void SetMany(IEnumerable<SyncEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = UpsertSql;

            // Parameters are created once and re-bound per row so the statement is prepared once.
            var key = cmd.Parameters.Add(new SqliteParameter("$key", SqliteType.Text));
            var etag = cmd.Parameters.Add(new SqliteParameter("$etag", SqliteType.Text));
            var size = cmd.Parameters.Add(new SqliteParameter("$size", SqliteType.Integer));
            var remote = cmd.Parameters.Add(new SqliteParameter("$remote", SqliteType.Integer));
            var local = cmd.Parameters.Add(new SqliteParameter("$local", SqliteType.Integer));

            foreach (var entry in entries)
            {
                key.Value = entry.Key;
                etag.Value = (object?)entry.ETag ?? DBNull.Value;
                size.Value = entry.Size;
                remote.Value = entry.RemoteModifiedUtcTicks;
                local.Value = entry.LocalModifiedUtcTicks;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM entries WHERE key = $k;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Removes many keys in one transaction (a folder delete cascades to all of them).</summary>
    public void RemoveMany(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM entries WHERE key = $k;";
            var p = cmd.Parameters.Add(new SqliteParameter("$k", SqliteType.Text));
            foreach (var key in keys)
            {
                p.Value = key;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>
    /// Every tracked key starting with <paramref name="prefix"/>, in key order. Bounded by the
    /// subtree, not the bucket: callers that only need one folder must not pay for the whole map.
    /// </summary>
    public IReadOnlyList<string> KeysWithPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var results = new List<string>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            AddPrefixRange(cmd, "SELECT key FROM entries", prefix, " ORDER BY key;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) results.Add(reader.GetString(0));
        }
        return results;
    }

    /// <summary>
    /// Every tracked entry starting with <paramref name="prefix"/>. Used by the per-directory
    /// reconcile to see what it previously knew about a folder without touching any other folder.
    /// </summary>
    public IReadOnlyList<SyncEntry> EntriesWithPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var results = new List<SyncEntry>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            AddPrefixRange(
                cmd, "SELECT key, etag, size, remote_ticks, local_ticks FROM entries", prefix,
                " ORDER BY key;");
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) results.Add(Read(reader));
        }
        return results;
    }

    /// <summary>
    /// Records that a directory's contents have been listed, so later passes know it is worth
    /// reconciling. Safe to call repeatedly; the timestamp is refreshed.
    /// </summary>
    public void MarkDirectoryListed(string prefix, DateTime listedUtc)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "INSERT INTO directories (prefix, listed_ticks) VALUES ($p, $t) " +
                "ON CONFLICT(prefix) DO UPDATE SET listed_ticks = excluded.listed_ticks;";
            cmd.Parameters.AddWithValue("$p", prefix);
            cmd.Parameters.AddWithValue("$t", listedUtc.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Every directory prefix listed so far, shallowest first.</summary>
    public IReadOnlyList<string> ListedDirectories()
    {
        var results = new List<string>();
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT prefix FROM directories ORDER BY LENGTH(prefix), prefix;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) results.Add(reader.GetString(0));
        }
        return results;
    }

    /// <summary>
    /// Forgets that a directory and its sub-directories were ever listed, without touching what is
    /// known about the objects in them. Used when a folder is renamed or removed locally: the
    /// listing is stale, but the per-object state is still needed to move or delete them.
    /// </summary>
    public void ForgetDirectoryListings(string prefix) => DeleteByPrefix("directories", "prefix", prefix);

    /// <summary>
    /// Forgets a directory and everything recorded beneath it — used when a folder is gone for
    /// good, so the next pass does not keep reconciling a prefix that is no longer there.
    /// </summary>
    public void ForgetDirectoryTree(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            DeleteByPrefix("directories", "prefix", prefix, tx);
            DeleteByPrefix("entries", "key", prefix, tx);
            tx.Commit();
        }
    }

    private void DeleteByPrefix(string table, string column, string prefix, SqliteTransaction? tx = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        if (tx is null) { lock (_gate) { Run(); } } else { Run(); }

        void Run()
        {
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = prefix.Length == 0
                ? $"DELETE FROM {table};"
                : $"DELETE FROM {table} WHERE {column} >= $lo AND {column} < $hi;";
            if (prefix.Length > 0)
            {
                cmd.Parameters.AddWithValue("$lo", prefix);
                cmd.Parameters.AddWithValue("$hi", PrefixUpperBound(prefix));
            }
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Number of tracked keys (diagnostics and logging).</summary>
    public long Count
    {
        get
        {
            lock (_gate)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM entries;";
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
        }
    }

    private const string UpsertSql =
        "INSERT INTO entries (key, etag, size, remote_ticks, local_ticks) " +
        "VALUES ($key, $etag, $size, $remote, $local) " +
        "ON CONFLICT(key) DO UPDATE SET " +
        "  etag = excluded.etag, size = excluded.size," +
        "  remote_ticks = excluded.remote_ticks, local_ticks = excluded.local_ticks;";

    private static void Bind(SqliteCommand cmd, SyncEntry e)
    {
        cmd.Parameters.AddWithValue("$key", e.Key);
        cmd.Parameters.AddWithValue("$etag", (object?)e.ETag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size", e.Size);
        cmd.Parameters.AddWithValue("$remote", e.RemoteModifiedUtcTicks);
        cmd.Parameters.AddWithValue("$local", e.LocalModifiedUtcTicks);
    }

    private static SyncEntry Read(SqliteDataReader r) => new()
    {
        Key = r.GetString(0),
        ETag = r.IsDBNull(1) ? null : r.GetString(1),
        Size = r.GetInt64(2),
        RemoteModifiedUtcTicks = r.GetInt64(3),
        LocalModifiedUtcTicks = r.GetInt64(4),
    };

    /// <summary>
    /// Builds a half-open range scan over the primary key rather than a LIKE/GLOB, so SQLite walks
    /// only the matching slice of the index. An empty prefix means "everything", which needs no
    /// bounds at all.
    /// </summary>
    private static void AddPrefixRange(SqliteCommand cmd, string select, string prefix, string tail)
    {
        if (prefix.Length == 0)
        {
            cmd.CommandText = select + tail;
            return;
        }

        cmd.CommandText = select + " WHERE key >= $lo AND key < $hi" + tail;
        cmd.Parameters.AddWithValue("$lo", prefix);
        cmd.Parameters.AddWithValue("$hi", PrefixUpperBound(prefix));
    }

    /// <summary>
    /// The exclusive upper bound of a prefix range: the prefix with its last character bumped by
    /// one. char.MaxValue cannot be bumped, so those (never real S3 keys) fall back to appending.
    /// </summary>
    internal static string PrefixUpperBound(string prefix)
    {
        var last = prefix[^1];
        return last == char.MaxValue
            ? prefix + char.MaxValue
            : prefix[..^1] + (char)(last + 1);
    }

    /// <summary>
    /// One-time import of the pre-0.9 JSON state file. The JSON is renamed rather than deleted so
    /// a failed import stays recoverable by hand.
    /// </summary>
    private void MigrateLegacyJson(string jsonPath)
    {
        try
        {
            if (!File.Exists(jsonPath)) return;

            var list = JsonSerializer.Deserialize<List<SyncEntry>>(File.ReadAllText(jsonPath));
            if (list is not null)
                SetMany(list.Where(e => !string.IsNullOrEmpty(e.Key)));

            File.Move(jsonPath, jsonPath + ".migrated", overwrite: true);
        }
        catch { /* a corrupt or unreadable legacy file just means starting from the bucket */ }
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Fold the WAL back into the database file so the sidecar files don't outlive us.
            try { Execute("PRAGMA wal_checkpoint(TRUNCATE);"); } catch { /* best-effort */ }
            _db.Dispose();
        }
    }
}
