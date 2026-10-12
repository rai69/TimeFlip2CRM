using System.ComponentModel.DataAnnotations;

namespace TimeflipCrmSync.Config;

public sealed class TimeFlipOptions
{
    public const string SectionName = "TimeFlip";

    /// <summary>Base URL of the TimeFlip cloud API.</summary>
    public string BaseUrl { get; set; } = "https://newapi.timeflip.io";

    [Required]
    public string Email { get; set; } = "";

    [Required]
    public string Password { get; set; } = "";
}

public sealed class CrmOptions
{
    public const string SectionName = "Crm";

    /// <summary>Root of the CRM; the API lives under <c>/api/v1</c>.</summary>
    public string BaseUrl { get; set; } = "https://crm.green-cloud.nl";

    /// <summary>
    /// Personal API token (<c>crm_pat_…</c>) from Profile → API tokens, with the
    /// scopes time.read, time.write and links.read. Entries are written as the
    /// token's owner, so no user id is needed.
    /// </summary>
    [Required]
    public string Token { get; set; } = "";

    /// <summary>
    /// The organization's time zone. The CRM takes a local date plus HH:mm times,
    /// so TimeFlip's UTC instants are converted to this zone before sending.
    /// </summary>
    [Required]
    public string TimeZone { get; set; } = "Europe/Amsterdam";

    public TimeZoneInfo ResolveTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public bool HasValidTimeZone()
    {
        try
        {
            ResolveTimeZone();
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>How often the worker runs a sync pass.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// How far back a scheduled pass looks, to catch late edits in TimeFlip.
    /// Kept small so the hourly pass stays cheap; a manual trigger can ask for a
    /// wider window (see <see cref="MaxManualLookbackDays"/>).
    /// </summary>
    public int LookbackDays { get; set; } = 1;

    /// <summary>
    /// Largest window a manual <c>POST /sync?lookbackDays=N</c> may request.
    /// Scheduled passes always use <see cref="LookbackDays"/>.
    /// </summary>
    public int MaxManualLookbackDays { get; set; } = 90;

    /// <summary>
    /// Ignore intervals shorter than this, filtering out accidental cube flips.
    /// Default 60s; the CRM works in whole minutes anyway.
    /// </summary>
    public int MinIntervalSeconds { get; set; } = 60;

    /// <summary>When true, compute what would be created but do not call the CRM.</summary>
    public bool DryRun { get; set; } = false;

    /// <summary>Path to the SQLite state file (mounted volume in Docker).</summary>
    public string StateDbPath { get; set; } = "/data/sync-state.db";

    /// <summary>
    /// When true, TimeFlip tasks with no matching mapping rule are skipped
    /// instead of failing the pass.
    /// </summary>
    public bool SkipUnmapped { get; set; } = true;

    /// <summary>Run one sync pass immediately on startup instead of waiting a full interval.</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>
    /// Optional shared secret protecting POST /sync. When set, callers must send it
    /// in an "X-Trigger-Token" header. When empty, the endpoint is unauthenticated
    /// (fine only behind a trusted network).
    /// </summary>
    public string TriggerToken { get; set; } = "";
}
