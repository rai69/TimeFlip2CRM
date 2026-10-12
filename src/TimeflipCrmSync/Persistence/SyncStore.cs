using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;

namespace TimeflipCrmSync.Persistence;

/// <summary>
/// What one TimeFlip interval became in the CRM. Usually one entry; two when the
/// interval ran past midnight and the CRM split it per day. The first id is the
/// entry that gets updated when the interval is edited.
/// </summary>
public sealed record SyncedEntry(
    string TimeFlipIntervalId,
    IReadOnlyList<string> CrmEntryIds,
    string ContentHash);

/// <summary>
/// Maps each synced TimeFlip interval to the CRM time entries it produced, so
/// re-runs are idempotent. Backed by a SQLite file on a mounted volume.
/// </summary>
public sealed class SyncStore
{
    private readonly string _connectionString;

    public SyncStore(IOptions<SyncOptions> options)
    {
        var path = options.Value.StateDbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS synced_entries (
                timeflip_interval_id TEXT PRIMARY KEY,
                crm_entry_ids        TEXT NOT NULL,
                content_hash         TEXT NOT NULL,
                synced_at            TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public SyncedEntry? Find(string timeFlipIntervalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT timeflip_interval_id, crm_entry_ids, content_hash
            FROM synced_entries WHERE timeflip_interval_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", timeFlipIntervalId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new SyncedEntry(
            reader.GetString(0),
            reader.GetString(1).Split(',', StringSplitOptions.RemoveEmptyEntries),
            reader.GetString(2));
    }

    public void Upsert(SyncedEntry entry, DateTimeOffset now)
    {
        if (entry.CrmEntryIds.Count == 0)
            throw new ArgumentException("A synced interval needs at least one CRM entry id.", nameof(entry));

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO synced_entries
                (timeflip_interval_id, crm_entry_ids, content_hash, synced_at)
            VALUES ($id, $crm, $hash, $ts)
            ON CONFLICT(timeflip_interval_id) DO UPDATE SET
                crm_entry_ids = excluded.crm_entry_ids,
                content_hash  = excluded.content_hash,
                synced_at     = excluded.synced_at;
            """;
        cmd.Parameters.AddWithValue("$id", entry.TimeFlipIntervalId);
        cmd.Parameters.AddWithValue("$crm", string.Join(',', entry.CrmEntryIds));
        cmd.Parameters.AddWithValue("$hash", entry.ContentHash);
        cmd.Parameters.AddWithValue("$ts", now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void Delete(string timeFlipIntervalId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM synced_entries WHERE timeflip_interval_id = $id;";
        cmd.Parameters.AddWithValue("$id", timeFlipIntervalId);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.ApplyConcurrencyPragmas();
        return conn;
    }
}
