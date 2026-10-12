using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;

namespace TimeflipCrmSync.Persistence;

/// <summary>
/// One stored mapping rule. Same fields as <see cref="TaskMappingRule"/> plus
/// the database identity and the position that decides match order.
/// </summary>
public sealed record MappingRuleRecord
{
    public long Id { get; init; }
    public int SortOrder { get; init; }
    public string? TaskId { get; init; }
    public string? TaskName { get; init; }
    public CrmLinkKind? LinkKind { get; init; }
    public string? LinkId { get; init; }
    public string? LinkName { get; init; }
    public bool? Billable { get; init; }
    public string? DescriptionTemplate { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public TaskMappingRule ToRule() => new()
    {
        TaskId = TaskId,
        TaskName = TaskName,
        LinkKind = LinkKind,
        LinkId = LinkId,
        LinkName = LinkName,
        Billable = Billable,
        DescriptionTemplate = DescriptionTemplate,
    };
}

/// <summary>
/// SQLite-backed store for the task mapping rules that the web UI edits. Lives
/// in the same database file as <see cref="SyncStore"/> (the writable /data
/// volume).
///
/// Rules are read fresh at the start of every sync pass, so edits take effect
/// without restarting the service.
/// </summary>
public sealed class MappingStore
{
    private const string Columns = """
        id, sort_order, task_id, task_name, link_kind, link_id, link_name,
        billable, description_template, updated_at
        """;

    private readonly string _connectionString;

    public MappingStore(IOptions<SyncOptions> options)
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
            CREATE TABLE IF NOT EXISTS mapping_rules (
                id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                sort_order           INTEGER NOT NULL DEFAULT 0,
                task_id              TEXT NULL,
                task_name            TEXT NULL,
                link_kind            TEXT NULL,
                link_id              TEXT NULL,
                link_name            TEXT NULL,
                billable             INTEGER NULL,
                description_template TEXT NULL,
                updated_at           TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MappingRuleRecord> GetAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM mapping_rules ORDER BY sort_order, id;";

        var result = new List<MappingRuleRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    public MappingRuleRecord? Get(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM mapping_rules WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Appends a rule at the end of the match order and returns its id.</summary>
    public long Insert(TaskMappingRule rule, DateTimeOffset now)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mapping_rules
                (sort_order, task_id, task_name, link_kind, link_id, link_name,
                 billable, description_template, updated_at)
            VALUES
                ((SELECT COALESCE(MAX(sort_order), 0) + 1 FROM mapping_rules),
                 $taskId, $taskName, $linkKind, $linkId, $linkName,
                 $billable, $template, $ts)
            RETURNING id;
            """;
        Bind(cmd, rule, now);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Overwrites a rule in place, keeping its position. False when unknown.</summary>
    public bool Update(long id, TaskMappingRule rule, DateTimeOffset now)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE mapping_rules SET
                task_id              = $taskId,
                task_name            = $taskName,
                link_kind            = $linkKind,
                link_id              = $linkId,
                link_name            = $linkName,
                billable             = $billable,
                description_template = $template,
                updated_at           = $ts
            WHERE id = $id;
            """;
        Bind(cmd, rule, now);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool Delete(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM mapping_rules WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// The stored rules as the config object the mapper consumes, in match order.
    /// </summary>
    public MappingConfig ToMappingConfig(bool skipUnmapped) => new()
    {
        SkipUnmapped = skipUnmapped,
        Rules = GetAll().Select(r => r.ToRule()).ToList(),
    };

    private static void Bind(SqliteCommand cmd, TaskMappingRule rule, DateTimeOffset now)
    {
        cmd.Parameters.AddWithValue("$taskId", (object?)Trim(rule.TaskId) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$taskName", (object?)Trim(rule.TaskName) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$linkKind",
            rule.LinkKind is { } kind ? kind.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$linkId", (object?)Trim(rule.LinkId) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$linkName", (object?)Trim(rule.LinkName) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$billable",
            rule.Billable is { } b ? (b ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$template",
            (object?)Trim(rule.DescriptionTemplate) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", now.ToString("O"));
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static MappingRuleRecord Read(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        SortOrder = r.GetInt32(1),
        TaskId = r.IsDBNull(2) ? null : r.GetString(2),
        TaskName = r.IsDBNull(3) ? null : r.GetString(3),
        LinkKind = !r.IsDBNull(4) && Enum.TryParse<CrmLinkKind>(r.GetString(4), out var kind)
            ? kind : null,
        LinkId = r.IsDBNull(5) ? null : r.GetString(5),
        LinkName = r.IsDBNull(6) ? null : r.GetString(6),
        Billable = r.IsDBNull(7) ? null : r.GetInt64(7) != 0,
        DescriptionTemplate = r.IsDBNull(8) ? null : r.GetString(8),
        UpdatedAt = DateTimeOffset.Parse(r.GetString(9)),
    };

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.ApplyConcurrencyPragmas();
        return conn;
    }
}
