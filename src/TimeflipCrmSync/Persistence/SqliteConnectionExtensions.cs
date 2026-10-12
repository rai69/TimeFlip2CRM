using Microsoft.Data.Sqlite;

namespace TimeflipCrmSync.Persistence;

internal static class SqliteConnectionExtensions
{
    /// <summary>
    /// WAL lets the web UI write mapping rules while a sync pass reads, instead
    /// of the two locking each other out; busy_timeout makes the loser of a
    /// write race wait rather than fail with SQLITE_BUSY.
    /// </summary>
    public static void ApplyConcurrencyPragmas(this SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
    }
}
