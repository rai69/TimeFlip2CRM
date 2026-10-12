namespace TimeflipCrmSync.TimeFlip.Models;

/// <summary>
/// A task configured on the TimeFlip cube. <see cref="Id"/> is the same key
/// that <see cref="TimeFlipInterval.TaskId"/> carries, so a mapping rule stored
/// against it will match the intervals that task produces.
/// </summary>
public sealed record TimeFlipTask(string Id, string Name, bool Billable);

/// <summary>
/// A single tracked interval from TimeFlip, normalized for our use.
/// This is the shape the rest of the app depends on; the raw API DTOs live
/// in <see cref="TimeFlipClient"/> and are mapped into this.
/// </summary>
public sealed record TimeFlipInterval
{
    /// <summary>Stable id of the tracked interval as reported by TimeFlip.</summary>
    public required string Id { get; init; }

    public required string TaskId { get; init; }
    public required string TaskName { get; init; }

    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }

    public bool? Billable { get; init; }

    public TimeSpan Duration => End - Start;

    /// <summary>
    /// Deterministic fingerprint of the content that matters for the CRM.
    /// Used to detect when a synced interval was edited in TimeFlip and needs
    /// its CRM entry updated. Billable is left out: the CRM entry's billable
    /// comes from the mapping rule or the CRM's own default, not the cube.
    /// </summary>
    public string ContentHash()
    {
        var raw = $"{TaskId}|{Start:O}|{End:O}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}
