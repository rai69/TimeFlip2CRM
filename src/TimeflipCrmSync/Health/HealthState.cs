using TimeflipCrmSync.Sync;

namespace TimeflipCrmSync.Health;

/// <summary>
/// Shared, thread-safe snapshot of the worker's last sync outcome. The worker
/// writes to it; the health check reads from it. Singleton.
/// </summary>
public sealed class HealthState
{
    private readonly Lock _gate = new();

    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public DateTimeOffset? LastSuccessAt { get; private set; }
    public SyncSummary? LastSummary { get; private set; }
    public string? LastError { get; private set; }

    public HealthState(TimeProvider clock) => StartedAt = clock.GetUtcNow();

    public void RecordSuccess(SyncSummary summary, DateTimeOffset now)
    {
        lock (_gate)
        {
            LastRunAt = now;
            LastSuccessAt = now;
            LastSummary = summary;
            LastError = null;
        }
    }

    public void RecordFailure(string error, DateTimeOffset now)
    {
        lock (_gate)
        {
            LastRunAt = now;
            LastError = error;
        }
    }

    public HealthSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new HealthSnapshot(
                StartedAt, LastRunAt, LastSuccessAt, LastSummary, LastError);
        }
    }
}

public sealed record HealthSnapshot(
    DateTimeOffset StartedAt,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? LastSuccessAt,
    SyncSummary? LastSummary,
    string? LastError);
