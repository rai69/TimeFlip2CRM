using TimeflipCrmSync.Health;

namespace TimeflipCrmSync.Sync;

/// <summary>
/// Runs a single sync pass in a fresh DI scope, records the outcome into
/// <see cref="HealthState"/>, and serializes passes so a scheduled tick and a
/// manual <c>POST /sync</c> can never overlap. Singleton.
/// </summary>
public sealed class SyncRunner
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HealthState _health;
    private readonly TimeProvider _clock;
    private readonly ILogger<SyncRunner> _log;

    public SyncRunner(IServiceScopeFactory scopeFactory, HealthState health,
        TimeProvider clock, ILogger<SyncRunner> log)
    {
        _scopeFactory = scopeFactory;
        _health = health;
        _clock = clock;
        _log = log;
    }

    /// <summary>True while a pass is in progress.</summary>
    public bool IsRunning => _gate.CurrentCount == 0;

    /// <summary>
    /// Run one pass. When <paramref name="waitIfBusy"/> is false and a pass is
    /// already running, returns null immediately instead of queuing.
    /// <paramref name="lookbackDays"/> overrides the configured window for this
    /// pass only; null uses SyncOptions.LookbackDays.
    /// Records health and rethrows on failure.
    /// </summary>
    public async Task<SyncSummary?> RunAsync(bool waitIfBusy, int? lookbackDays,
        CancellationToken ct)
    {
        if (waitIfBusy)
            await _gate.WaitAsync(ct);
        else if (!await _gate.WaitAsync(0, ct))
            return null;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
            var summary = await sync.RunAsync(lookbackDays, ct);
            _health.RecordSuccess(summary, _clock.GetUtcNow());
            return summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sync pass failed.");
            _health.RecordFailure(ex.Message, _clock.GetUtcNow());
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
