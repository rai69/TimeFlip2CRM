using System.Net;
using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Crm;
using TimeflipCrmSync.Persistence;
using TimeflipCrmSync.TimeFlip;

namespace TimeflipCrmSync.Sync;

public sealed record SyncSummary(int Created, int Updated, int Skipped, int Recreated, int Errors)
{
    public override string ToString() =>
        $"created={Created} updated={Updated} recreated={Recreated} skipped={Skipped} errors={Errors}";
}

/// <summary>
/// One sync pass: fetch TimeFlip intervals over the lookback window, map them,
/// and create/update the matching CRM time entries idempotently.
/// </summary>
public sealed class SyncService
{
    /// <summary>
    /// Margin around the (UTC) sync window when listing CRM entries: an interval
    /// that overlaps the window can start up to a day before it, and its entries
    /// carry local dates that may fall a day either side.
    /// </summary>
    private const int EntryListMarginDays = 2;

    private readonly ITimeFlipClient _timeflip;
    private readonly ICrmClient _crm;
    private readonly SyncStore _store;
    private readonly TimeEntryMapper _mapper;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncService> _log;
    private readonly TimeProvider _clock;

    public SyncService(
        ITimeFlipClient timeflip,
        ICrmClient crm,
        SyncStore store,
        TimeEntryMapper mapper,
        IOptions<SyncOptions> options,
        ILogger<SyncService> log,
        TimeProvider clock)
    {
        _timeflip = timeflip;
        _crm = crm;
        _store = store;
        _mapper = mapper;
        _options = options.Value;
        _log = log;
        _clock = clock;
    }

    public Task<SyncSummary> RunAsync(CancellationToken ct) => RunAsync(null, ct);

    /// <summary>
    /// Run one pass. <paramref name="lookbackDays"/> overrides the configured
    /// window for this pass only — used by the manual trigger to re-scan further
    /// back than the scheduled passes do.
    /// </summary>
    public async Task<SyncSummary> RunAsync(int? lookbackDays, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var to = DateOnly.FromDateTime(now.UtcDateTime);
        var from = to.AddDays(-(lookbackDays ?? _options.LookbackDays));

        _log.LogInformation("Sync pass: fetching TimeFlip intervals {From}..{To}", from, to);
        var intervals = await _timeflip.GetIntervalsAsync(from, to, ct);
        _log.LogInformation("TimeFlip returned {Count} intervals.", intervals.Count);

        // Which CRM entries still exist. Fetched once, and only when a pass has
        // synced intervals to verify: one listing instead of a GET per entry
        // keeps wide manual re-scans well under the CRM's rate limit.
        IReadOnlySet<string>? crmIds = null;
        async Task<IReadOnlySet<string>> CrmIdsAsync() =>
            crmIds ??= await _crm.GetTimeEntryIdsAsync(
                from.AddDays(-EntryListMarginDays), to.AddDays(EntryListMarginDays), ct);

        int created = 0, updated = 0, skipped = 0, recreated = 0, errors = 0;

        foreach (var interval in intervals)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (interval.Duration.TotalSeconds < _options.MinIntervalSeconds)
                {
                    skipped++;
                    _log.LogDebug("Skipped {Sec}s interval below minimum ({Task}).",
                        (int)interval.Duration.TotalSeconds, interval.TaskName);
                    continue;
                }

                var mapped = _mapper.Map(interval);
                if (mapped is null)
                {
                    skipped++;
                    _log.LogDebug("Skipped unmapped task '{Task}'.", interval.TaskName);
                    continue;
                }

                var hash = interval.ContentHash();
                var existing = _store.Find(interval.Id);

                if (existing is null)
                {
                    await CreateAsync(interval.Id, hash, mapped, now, ct);
                    created++;
                }
                else if (existing.ContentHash == hash)
                {
                    var live = await CrmIdsAsync();
                    if (existing.CrmEntryIds.All(live.Contains))
                    {
                        skipped++;
                    }
                    else
                    {
                        // Deleted in the CRM (or half of a midnight split was):
                        // clear what's left and write the interval again whole.
                        _log.LogWarning("CRM entry {Ids} was deleted; recreating.",
                            string.Join(", ", existing.CrmEntryIds));
                        await DeleteAsync(existing.CrmEntryIds.Where(live.Contains), ct);
                        await CreateAsync(interval.Id, hash, mapped, now, ct);
                        recreated++;
                    }
                }
                else
                {
                    // Interval was edited in TimeFlip: update the existing entry.
                    await UpdateAsync(existing, hash, mapped, now, ct);
                    updated++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors++;
                _log.LogError(ex, "Failed to sync interval {Id} ({Task}).",
                    interval.Id, interval.TaskName);
            }
        }

        var summary = new SyncSummary(created, updated, skipped, recreated, errors);
        _log.LogInformation("Sync pass complete: {Summary}", summary);
        return summary;
    }

    private async Task CreateAsync(string intervalId, string hash, MapResult mapped,
        DateTimeOffset now, CancellationToken ct)
    {
        var r = mapped.Request;
        if (_options.DryRun)
        {
            _log.LogInformation("[dry-run] would create entry for interval {Id}: {Date} {Start}-{End} {Desc}",
                intervalId, r.Date, r.StartTime, r.EndTime, r.Description);
            return;
        }

        var entries = await _crm.CreateTimeEntryAsync(r, ct);
        var ids = entries.Select(e => e.Id).ToList();
        _store.Upsert(new SyncedEntry(intervalId, ids, hash), now);
        _log.LogInformation("Created CRM entry {Ids} for interval {Interval}.",
            string.Join(", ", ids), intervalId);
    }

    private async Task UpdateAsync(SyncedEntry existing, string hash, MapResult mapped,
        DateTimeOffset now, CancellationToken ct)
    {
        var primary = existing.CrmEntryIds[0];
        if (_options.DryRun)
        {
            _log.LogInformation("[dry-run] would update {Id}.", primary);
            return;
        }

        IReadOnlyList<string> ids;
        try
        {
            ids = (await _crm.UpdateTimeEntryAsync(primary, mapped.Request, ct))
                .Select(e => e.Id).ToList();
        }
        catch (CrmApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Deleted in the CRM as well as edited in TimeFlip: write it fresh.
            _log.LogWarning("CRM entry {Id} was deleted; recreating.", primary);
            ids = (await _crm.CreateTimeEntryAsync(mapped.Request, ct)).Select(e => e.Id).ToList();
        }

        // The CRM only ever adds a next-day entry on update; one left over from an
        // earlier midnight split that the edit no longer covers must go.
        await DeleteAsync(existing.CrmEntryIds.Except(ids, StringComparer.OrdinalIgnoreCase), ct);
        _store.Upsert(new SyncedEntry(existing.TimeFlipIntervalId, ids, hash), now);
    }

    private async Task DeleteAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        foreach (var id in ids.ToList())
        {
            if (_options.DryRun)
            {
                _log.LogInformation("[dry-run] would delete CRM entry {Id}.", id);
                continue;
            }

            await _crm.DeleteTimeEntryAsync(id, ct);
            _log.LogInformation("Deleted CRM entry {Id}.", id);
        }
    }
}
