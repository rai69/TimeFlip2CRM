using System.Collections.Concurrent;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Crm.Models;

namespace TimeflipCrmSync.Crm;

/// <summary>
/// Short-lived cache of the CRM link lookups that back the mapping UI's
/// pickers. The UI searches as you type, and the CRM allows 120 requests a
/// minute per token, so repeated searches (and every dialog opening) are served
/// from here for a few minutes. The UI can force a refresh.
///
/// Singleton; the CRM client is transient, so callers pass it in.
/// </summary>
public sealed class CrmLinkCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private const int MaxEntries = 500;

    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(CrmLinkKind, string), (IReadOnlyList<CrmLink> Value, DateTimeOffset At)> _entries = new();

    public CrmLinkCache(TimeProvider clock) => _clock = clock;

    public async Task<IReadOnlyList<CrmLink>> LookupAsync(ICrmClient client, CrmLinkKind kind,
        string? search, bool refresh, CancellationToken ct)
    {
        var key = (kind, (search ?? "").Trim().ToLowerInvariant());
        if (refresh)
            Clear(kind);
        else if (_entries.TryGetValue(key, out var hit) && _clock.GetUtcNow() - hit.At < Ttl)
            return hit.Value;

        var result = await client.LookupLinksAsync(kind, search, ct);

        // Search-as-you-type creates a key per keystroke; drop everything rather
        // than tracking recency, the cache refills in a few requests.
        if (_entries.Count >= MaxEntries) _entries.Clear();
        _entries[key] = (result, _clock.GetUtcNow());
        return result;
    }

    private void Clear(CrmLinkKind kind)
    {
        foreach (var key in _entries.Keys.Where(k => k.Item1 == kind)) _entries.TryRemove(key, out _);
    }
}
