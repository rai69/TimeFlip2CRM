using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Persistence;
using TimeflipCrmSync.Sync;
using TimeflipCrmSync.TimeFlip.Models;
using Xunit;

namespace TimeflipCrmSync.Tests;

public sealed class SyncServiceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"sync-test-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Now =
        new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private const string ProjectId = "0199a3ef-aa11-7b22-8c33-d4e5f6a7b802";

    private static TimeFlipInterval SampleInterval() => new()
    {
        Id = "555",
        TaskId = "42",
        TaskName = "Dev",
        Start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
    };

    // 23:30–00:30 Amsterdam (CEST, UTC+2): the CRM splits it in two.
    private static TimeFlipInterval PastMidnightInterval() => SampleInterval() with
    {
        Start = new DateTimeOffset(2026, 7, 19, 21, 30, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 7, 19, 22, 30, 0, TimeSpan.Zero),
    };

    private static MappingConfig MappedConfig() => new()
    {
        SkipUnmapped = true,
        Rules = { new TaskMappingRule { TaskId = "42", LinkKind = CrmLinkKind.Project, LinkId = ProjectId } },
    };

    private FakeTimeFlipClient _timeflip = new([]);

    private (SyncService svc, FakeCrmClient crm, SyncStore store) Build(
        IReadOnlyList<TimeFlipInterval> intervals,
        MappingConfig mapping,
        bool dryRun = false,
        FakeCrmClient? crm = null)
    {
        var syncOptions = Options.Create(new SyncOptions
        {
            StateDbPath = _dbPath,
            LookbackDays = 1,
            DryRun = dryRun,
        });
        var store = new SyncStore(syncOptions);
        store.Initialize();

        var mapper = new TimeEntryMapper(mapping, new CrmOptions());
        var fake = crm ?? new FakeCrmClient();
        _timeflip = new FakeTimeFlipClient(intervals);

        var svc = new SyncService(
            _timeflip,
            fake,
            store,
            mapper,
            syncOptions,
            NullLogger<SyncService>.Instance,
            new StubClock(Now));

        return (svc, fake, store);
    }

    [Fact]
    public async Task New_interval_is_created_and_recorded()
    {
        var iv = SampleInterval();
        var (svc, crm, store) = Build([iv], MappedConfig());

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Created);
        Assert.Equal(1, crm.CreateCount);
        Assert.Equal(ProjectId, crm.Created[0].ProjectId);
        Assert.Equal("11:00", crm.Created[0].StartTime); // 09:00 UTC in Amsterdam summer time
        var stored = store.Find(iv.Id);
        Assert.NotNull(stored);
        Assert.Equal(iv.ContentHash(), stored!.ContentHash);
    }

    [Fact]
    public async Task A_session_past_midnight_records_both_crm_entries()
    {
        var iv = PastMidnightInterval();
        var (svc, crm, store) = Build([iv], MappedConfig());

        await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, crm.CreateCount);
        Assert.Equal(2, store.Find(iv.Id)!.CrmEntryIds.Count);
    }

    [Fact]
    public async Task Configured_lookback_is_used_when_no_override_is_given()
    {
        var (svc, _, _) = Build([], MappedConfig());

        await svc.RunAsync(CancellationToken.None);

        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        Assert.Equal((today.AddDays(-1), today), _timeflip.LastRange);
    }

    [Fact]
    public async Task Manual_lookback_override_widens_the_window()
    {
        var (svc, _, _) = Build([], MappedConfig());

        await svc.RunAsync(30, CancellationToken.None);

        var today = DateOnly.FromDateTime(Now.UtcDateTime);
        Assert.Equal((today.AddDays(-30), today), _timeflip.LastRange);
    }

    [Fact]
    public async Task Unchanged_and_existing_is_skipped()
    {
        var iv = SampleInterval();
        var crm = new FakeCrmClient();
        crm.ExistingIds.Add("crm-1");
        var (svc, _, store) = Build([iv], MappedConfig(), crm: crm);
        store.Upsert(new SyncedEntry(iv.Id, ["crm-1"], iv.ContentHash()), Now);

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, crm.CreateCount);
        Assert.Equal(0, crm.UpdateCount);
    }

    [Fact]
    public async Task Existence_is_checked_with_one_listing_per_pass()
    {
        var a = SampleInterval() with { Id = "1" };
        var b = SampleInterval() with { Id = "2" };
        var crm = new FakeCrmClient();
        crm.ExistingIds.UnionWith(["crm-1", "crm-2"]);
        var (svc, _, store) = Build([a, b], MappedConfig(), crm: crm);
        store.Upsert(new SyncedEntry("1", ["crm-1"], a.ContentHash()), Now);
        store.Upsert(new SyncedEntry("2", ["crm-2"], b.ContentHash()), Now);

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(2, summary.Skipped);
        Assert.Equal(1, crm.ListCount);
    }

    [Fact]
    public async Task Nothing_to_verify_means_no_listing()
    {
        var (svc, crm, _) = Build([SampleInterval()], MappedConfig());

        await svc.RunAsync(CancellationToken.None);

        Assert.Equal(0, crm.ListCount);
    }

    [Fact]
    public async Task Unchanged_but_deleted_in_crm_is_recreated()
    {
        var iv = SampleInterval();
        var crm = new FakeCrmClient(); // "crm-1" NOT in ExistingIds
        var (svc, _, store) = Build([iv], MappedConfig(), crm: crm);
        store.Upsert(new SyncedEntry(iv.Id, ["crm-1"], iv.ContentHash()), Now);

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Recreated);
        Assert.Equal(1, crm.CreateCount);
        // store now points at the newly created id
        Assert.DoesNotContain("crm-1", store.Find(iv.Id)!.CrmEntryIds);
    }

    [Fact]
    public async Task Half_of_a_split_deleted_in_crm_clears_the_rest_and_recreates()
    {
        var iv = PastMidnightInterval();
        var crm = new FakeCrmClient();
        crm.ExistingIds.Add("crm-day1"); // "crm-day2" was deleted by hand
        var (svc, _, store) = Build([iv], MappedConfig(), crm: crm);
        store.Upsert(new SyncedEntry(iv.Id, ["crm-day1", "crm-day2"], iv.ContentHash()), Now);

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Recreated);
        Assert.Equal(["crm-day1"], crm.Deleted);
        Assert.Equal(1, crm.CreateCount);
        Assert.Equal(2, store.Find(iv.Id)!.CrmEntryIds.Count);
    }

    [Fact]
    public async Task Edited_interval_updates_existing_entry()
    {
        var iv = SampleInterval();
        var crm = new FakeCrmClient();
        crm.ExistingIds.Add("crm-1");
        var (svc, _, store) = Build([iv], MappedConfig(), crm: crm);
        // stored hash differs from current -> treated as edited
        store.Upsert(new SyncedEntry(iv.Id, ["crm-1"], "OLD-HASH"), Now);

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Updated);
        Assert.Equal(1, crm.UpdateCount);
        Assert.Equal("crm-1", crm.Updated[0].Id);
        Assert.Equal(iv.ContentHash(), store.Find(iv.Id)!.ContentHash);
        Assert.Empty(crm.Deleted);
    }

    [Fact]
    public async Task An_edit_that_no_longer_runs_past_midnight_deletes_the_next_day_entry()
    {
        var iv = SampleInterval();
        var crm = new FakeCrmClient();
        crm.ExistingIds.UnionWith(["crm-day1", "crm-day2"]);
        var (svc, _, store) = Build([iv], MappedConfig(), crm: crm);
        store.Upsert(new SyncedEntry(iv.Id, ["crm-day1", "crm-day2"], "OLD-HASH"), Now);

        await svc.RunAsync(CancellationToken.None);

        Assert.Equal("crm-day1", crm.Updated[0].Id);
        Assert.Equal(["crm-day2"], crm.Deleted);
        Assert.Equal(["crm-day1"], store.Find(iv.Id)!.CrmEntryIds);
    }

    [Fact]
    public async Task Edited_interval_whose_entry_was_deleted_is_created_again()
    {
        var iv = SampleInterval();
        var crm = new FakeCrmClient(); // "crm-1" is gone: update answers 404
        var (svc, _, store) = Build([iv], MappedConfig(), crm: crm);
        store.Upsert(new SyncedEntry(iv.Id, ["crm-1"], "OLD-HASH"), Now);

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Updated);
        Assert.Equal(0, summary.Errors);
        Assert.Equal(1, crm.CreateCount);
        Assert.DoesNotContain("crm-1", store.Find(iv.Id)!.CrmEntryIds);
    }

    [Fact]
    public async Task Unmapped_interval_is_skipped_without_creating()
    {
        var iv = SampleInterval();
        var (svc, crm, store) = Build([iv], new MappingConfig { SkipUnmapped = true });

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, crm.CreateCount);
        Assert.Null(store.Find(iv.Id));
    }

    [Fact]
    public async Task Short_interval_is_skipped()
    {
        var iv = SampleInterval() with { End = SampleInterval().Start.AddSeconds(59) };
        var (svc, crm, _) = Build([iv], MappedConfig());

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, crm.CreateCount);
    }

    [Fact]
    public async Task DryRun_counts_but_does_not_write_or_record()
    {
        var iv = SampleInterval();
        var (svc, crm, store) = Build([iv], MappedConfig(), dryRun: true);

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(1, summary.Created);
        Assert.Equal(0, crm.CreateCount);
        Assert.Null(store.Find(iv.Id));
    }

    [Fact]
    public async Task DryRun_never_deletes()
    {
        var iv = PastMidnightInterval();
        var crm = new FakeCrmClient();
        crm.ExistingIds.Add("crm-day1");
        var (svc, _, store) = Build([iv], MappedConfig(), dryRun: true, crm: crm);
        store.Upsert(new SyncedEntry(iv.Id, ["crm-day1", "crm-day2"], iv.ContentHash()), Now);

        await svc.RunAsync(CancellationToken.None);

        Assert.Empty(crm.Deleted);
        Assert.Equal(0, crm.CreateCount);
    }

    [Fact]
    public async Task Crm_failure_is_counted_as_error_and_others_continue()
    {
        var good = SampleInterval() with { Id = "1", TaskId = "42" };
        var bad = SampleInterval() with { Id = "2", TaskId = "42" };
        var crm = new FakeCrmClient { ThrowOnCreate = true };
        var (svc, _, store) = Build([good, bad], MappedConfig(), crm: crm);

        var summary = await svc.RunAsync(CancellationToken.None);

        Assert.Equal(2, summary.Errors);
        Assert.Equal(0, summary.Created);
        Assert.Null(store.Find("1"));
        Assert.Null(store.Find("2"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}
