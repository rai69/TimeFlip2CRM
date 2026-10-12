using System.Globalization;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Crm.Models;
using TimeflipCrmSync.TimeFlip.Models;

namespace TimeflipCrmSync.Sync;

public sealed record MapResult(CrmTimeEntryRequest Request, TaskMappingRule Rule);

/// <summary>
/// Turns a TimeFlip interval into a CRM time-entry request using the configured
/// task mapping. Returns null when the task is unmapped and the config says to
/// skip unmapped tasks.
///
/// The CRM takes a local date and HH:mm times in the organization's time zone,
/// so the interval's instants are rounded to the nearest minute and converted.
/// A session past midnight is sent as-is (end before start); the CRM splits it
/// into one entry per day.
/// </summary>
public sealed class TimeEntryMapper
{
    private const int MaxDescriptionLength = 1000;
    private static readonly TimeSpan MaxEntryLength = TimeSpan.FromHours(24);

    private readonly MappingConfig _mapping;
    private readonly TimeZoneInfo _zone;

    public TimeEntryMapper(MappingConfig mapping, CrmOptions crm)
    {
        _mapping = mapping;
        _zone = crm.ResolveTimeZone();
    }

    public MapResult? Map(TimeFlipInterval interval)
    {
        var rule = _mapping.Resolve(interval.TaskId, interval.TaskName);
        if (rule is null)
        {
            if (_mapping.SkipUnmapped) return null;
            throw new InvalidOperationException(
                $"No mapping rule for TimeFlip task '{interval.TaskName}' ({interval.TaskId}).");
        }

        var start = TimeZoneInfo.ConvertTime(RoundToMinute(interval.Start), _zone);
        var end = TimeZoneInfo.ConvertTime(RoundToMinute(interval.End), _zone);

        // The CRM can't express 24 h or more with times (start == end would be
        // ambiguous); such a session is nearly always a cube left on overnight.
        if (end - start >= MaxEntryLength)
            throw new InvalidOperationException(
                $"TimeFlip session for '{interval.TaskName}' starting {start:yyyy-MM-dd HH:mm} " +
                $"lasts {(end - start).TotalHours:0.#} h; the CRM only takes entries under 24 h. " +
                "Shorten or split it in TimeFlip.");

        var date = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var description = (rule.DescriptionTemplate ?? "{task}")
            .Replace("{task}", interval.TaskName)
            .Replace("{date}", date);
        if (description.Length > MaxDescriptionLength)
            description = description[..MaxDescriptionLength];

        return new MapResult(new CrmTimeEntryRequest
        {
            Date = date,
            StartTime = start.ToString("HH:mm", CultureInfo.InvariantCulture),
            EndTime = end.ToString("HH:mm", CultureInfo.InvariantCulture),
            Description = description,
            // Null is left out of the request, so the CRM applies the project's
            // or relation's default.
            Billable = rule.Billable,
            TaskId = rule.LinkKind == CrmLinkKind.Task ? rule.LinkId : null,
            ProjectId = rule.LinkKind == CrmLinkKind.Project ? rule.LinkId : null,
            RelationId = rule.LinkKind == CrmLinkKind.Relation ? rule.LinkId : null,
        }, rule);
    }

    /// <summary>
    /// Nearest whole minute. Rounding both ends the same way keeps any session
    /// of at least a minute at least a minute long, which the CRM requires.
    /// </summary>
    internal static DateTimeOffset RoundToMinute(DateTimeOffset value)
    {
        var shifted = value.AddSeconds(30);
        return new DateTimeOffset(
            shifted.Ticks - shifted.Ticks % TimeSpan.TicksPerMinute, shifted.Offset);
    }
}
