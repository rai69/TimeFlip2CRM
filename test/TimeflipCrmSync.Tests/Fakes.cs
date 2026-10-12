using System.Net;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Crm;
using TimeflipCrmSync.Crm.Models;
using TimeflipCrmSync.TimeFlip;
using TimeflipCrmSync.TimeFlip.Models;

namespace TimeflipCrmSync.Tests;

/// <summary>TimeProvider with a fixed, controllable "now".</summary>
internal sealed class StubClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class FakeTimeFlipClient(IReadOnlyList<TimeFlipInterval> intervals) : ITimeFlipClient
{
    public IReadOnlyList<TimeFlipTask> Tasks { get; set; } = [];

    /// <summary>The window the last GetIntervalsAsync call asked for.</summary>
    public (DateOnly From, DateOnly To)? LastRange { get; private set; }

    public Task<IReadOnlyList<TimeFlipInterval>> GetIntervalsAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        LastRange = (from, to);
        return Task.FromResult(intervals);
    }

    public Task<IReadOnlyList<TimeFlipTask>> GetTasksAsync(CancellationToken ct) =>
        Task.FromResult(Tasks);
}

/// <summary>
/// Records CRM calls and lets tests control which entry ids "exist" and
/// whether create should throw. Assigns sequential ids to created entries and,
/// like the CRM, splits an entry whose end is before its start into two.
/// </summary>
internal sealed class FakeCrmClient : ICrmClient
{
    private int _nextId = 1000;

    public List<CrmTimeEntryRequest> Created { get; } = new();
    public List<(string Id, CrmTimeEntryRequest Request)> Updated { get; } = new();
    public List<string> Deleted { get; } = new();
    public HashSet<string> ExistingIds { get; } = new();
    public bool ThrowOnCreate { get; set; }
    public int ListCount { get; private set; }

    public int CreateCount => Created.Count;
    public int UpdateCount => Updated.Count;

    public Task<IReadOnlyList<CrmTimeEntry>> CreateTimeEntryAsync(
        CrmTimeEntryRequest request, CancellationToken ct)
    {
        if (ThrowOnCreate) throw new InvalidOperationException("crm create failed");
        Created.Add(request);
        return Task.FromResult(Save(request, NewId()));
    }

    public Task<IReadOnlyList<CrmTimeEntry>> UpdateTimeEntryAsync(
        string id, CrmTimeEntryRequest request, CancellationToken ct)
    {
        if (!ExistingIds.Contains(id))
            throw new CrmApiException(HttpStatusCode.NotFound, "not_found", "gone");
        Updated.Add((id, request));
        return Task.FromResult(Save(request, id));
    }

    public Task DeleteTimeEntryAsync(string id, CancellationToken ct)
    {
        Deleted.Add(id);
        ExistingIds.Remove(id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<string>> GetTimeEntryIdsAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        ListCount++;
        return Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(ExistingIds));
    }

    public Dictionary<CrmLinkKind, List<CrmLink>> Links { get; } = new();
    public int LookupCount { get; private set; }

    public Task<IReadOnlyList<CrmLink>> LookupLinksAsync(
        CrmLinkKind kind, string? search, CancellationToken ct)
    {
        LookupCount++;
        var all = Links.GetValueOrDefault(kind) ?? [];
        return Task.FromResult<IReadOnlyList<CrmLink>>(all
            .Where(l => string.IsNullOrEmpty(search) ||
                        l.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList());
    }

    private string NewId() => (_nextId++).ToString();

    private IReadOnlyList<CrmTimeEntry> Save(CrmTimeEntryRequest request, string id)
    {
        ExistingIds.Add(id);
        var entries = new List<CrmTimeEntry> { new() { Id = id, Date = request.Date } };
        if (string.CompareOrdinal(request.EndTime, request.StartTime) < 0)
        {
            var next = NewId();
            ExistingIds.Add(next);
            entries.Add(new CrmTimeEntry { Id = next });
        }
        return entries;
    }
}
