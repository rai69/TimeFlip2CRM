using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;

namespace TimeflipCrmSync.Health;

/// <summary>
/// Reports the worker healthy while it is syncing on schedule:
/// - starting up (no run yet, within a grace window) => Healthy
/// - last pass threw                                  => Unhealthy
/// - no successful pass within 2 intervals + grace    => Unhealthy (stalled)
/// - otherwise                                        => Healthy
/// </summary>
public sealed class SyncHealthCheck : IHealthCheck
{
    private readonly HealthState _state;
    private readonly SyncOptions _options;
    private readonly TimeProvider _clock;

    public SyncHealthCheck(HealthState state, IOptions<SyncOptions> options, TimeProvider clock)
    {
        _state = state;
        _options = options.Value;
        _clock = clock;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var s = _state.Snapshot();
        var interval = TimeSpan.FromMinutes(_options.IntervalMinutes);
        var grace = TimeSpan.FromMinutes(5);

        var data = new Dictionary<string, object>
        {
            ["startedAt"] = s.StartedAt,
            ["lastRunAt"] = s.LastRunAt?.ToString("O") ?? "never",
            ["lastSuccessAt"] = s.LastSuccessAt?.ToString("O") ?? "never",
            ["lastSummary"] = s.LastSummary?.ToString() ?? "none",
        };

        if (s.LastRunAt is null)
        {
            return Task.FromResult(now - s.StartedAt <= interval + grace
                ? HealthCheckResult.Healthy("Starting up; first sync pending.", data)
                : HealthCheckResult.Unhealthy("No sync pass has completed yet.", data: data));
        }

        if (s.LastError is not null && s.LastSuccessAt != s.LastRunAt)
            data["lastError"] = s.LastError;

        if (s.LastSuccessAt is null)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No sync pass has ever succeeded.", data: data));

        var staleAfter = interval + interval + grace;
        if (now - s.LastSuccessAt > staleAfter)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"No successful sync in over {staleAfter.TotalMinutes:0} min.", data: data));

        return Task.FromResult(HealthCheckResult.Healthy("Syncing on schedule.", data));
    }
}
