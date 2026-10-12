using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Sync;

namespace TimeflipCrmSync;

/// <summary>
/// Background loop that runs a sync pass on a fixed interval via <see cref="SyncRunner"/>,
/// which serializes it against any manual POST /sync trigger.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly SyncRunner _runner;
    private readonly SyncOptions _options;
    private readonly ILogger<Worker> _log;

    public Worker(SyncRunner runner, IOptions<SyncOptions> options, ILogger<Worker> log)
    {
        _runner = runner;
        _options = options.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The database schema is created at startup in Program.cs.
        _log.LogInformation(
            "Worker started. Interval={Interval}min Lookback={Lookback}d DryRun={DryRun}",
            _options.IntervalMinutes, _options.LookbackDays, _options.DryRun);

        if (_options.RunOnStartup)
            await RunOnceSafe(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceSafe(stoppingToken);
    }

    private async Task RunOnceSafe(CancellationToken ct)
    {
        try
        {
            // Scheduled passes wait for any in-flight manual trigger to finish.
            await _runner.RunAsync(waitIfBusy: true, lookbackDays: null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // graceful shutdown
        }
        catch (Exception)
        {
            // Already logged and recorded by SyncRunner; keep looping.
        }
    }
}
