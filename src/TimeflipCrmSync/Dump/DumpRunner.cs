using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Sync;
using TimeflipCrmSync.TimeFlip;

namespace TimeflipCrmSync.Dump;

/// <summary>
/// Diagnostic startup mode (<c>--dump</c>): sign in to TimeFlip, fetch the
/// intervals over the lookback window, and print them together with
/// how each one would map to the CRM. Writes nothing to the CRM and does not
/// start the worker loop — used to confirm auth, the duration/startedAt
/// assumptions, and the task mapping before running for real.
/// </summary>
public static class DumpRunner
{
    private const int MaxRows = 50;

    /// <param name="lookbackDays">
    /// Window to fetch; null uses the configured (scheduled) lookback.
    /// </param>
    public static async Task RunAsync(IServiceProvider services, int? lookbackDays,
        CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        var timeflip = sp.GetRequiredService<ITimeFlipClient>();
        var sync = sp.GetRequiredService<IOptions<SyncOptions>>().Value;

        // A dump is how you inspect what the cube reports before any rules exist,
        // so tolerate an unreadable mapping: fall back to an empty ruleset (every
        // task then shows "SKIP (unmapped)").
        TimeEntryMapper mapper;
        try
        {
            mapper = sp.GetRequiredService<TimeEntryMapper>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(No usable mapping: {ex.Message} — showing tasks unmapped.)\n");
            // Use default CrmOptions so a first dump needs only TimeFlip
            // credentials (the CRM token may still be unset at this point).
            mapper = new TimeEntryMapper(
                new MappingConfig { SkipUnmapped = true },
                new CrmOptions());
        }

        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-(lookbackDays ?? sync.LookbackDays));

        Console.WriteLine($"Fetching TimeFlip intervals {from:yyyy-MM-dd}..{to:yyyy-MM-dd} (UTC)...");
        var intervals = await timeflip.GetIntervalsAsync(from, to, ct);
        Console.WriteLine($"Got {intervals.Count} interval(s). Showing up to {MaxRows}.\n");

        Console.WriteLine(
            $"{"Start (UTC)",-20} {"Duration",9}  {"Task",-24} Mapping (CRM local time)");
        Console.WriteLine(new string('-', 100));

        foreach (var iv in intervals.OrderBy(i => i.Start).Take(MaxRows))
        {
            string mapping;
            try
            {
                var m = mapper.Map(iv);
                mapping = m is null
                    ? "SKIP (unmapped)"
                    : $"{m.Request.Date} {m.Request.StartTime}-{m.Request.EndTime} " +
                      $"{m.Rule.LinkKind?.ToString().ToLowerInvariant()}=\"{m.Rule.LinkName ?? m.Rule.LinkId}\" " +
                      $"billable={m.Request.Billable?.ToString() ?? "default"} \"{m.Request.Description}\"";
            }
            catch (Exception ex)
            {
                mapping = $"ERROR: {ex.Message}";
            }

            var startStr = iv.Start.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
            Console.WriteLine(
                $"{startStr,-20} " +
                $"{FormatDuration(iv.Duration),9}  " +
                $"{Truncate(iv.TaskName, 24),-24} {mapping}");
        }

        if (intervals.Count > MaxRows)
            Console.WriteLine($"\n... {intervals.Count - MaxRows} more not shown.");

        Console.WriteLine(
            "\nSanity check: do the durations above match reality? " +
            "If a short session shows an absurd length, `duration` is not in seconds.");
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{d.Minutes}:{d.Seconds:D2}";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
