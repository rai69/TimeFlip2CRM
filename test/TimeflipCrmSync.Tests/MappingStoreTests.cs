using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Persistence;
using Xunit;

namespace TimeflipCrmSync.Tests;

public sealed class MappingStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"mapping-test-{Guid.NewGuid():N}.db");

    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private MappingStore Build()
    {
        var store = new MappingStore(Options.Create(new SyncOptions { StateDbPath = _dbPath }));
        store.Initialize();
        return store;
    }

    private static TaskMappingRule Rule(string name, string linkId = "r1") => new()
    {
        TaskName = name,
        LinkKind = CrmLinkKind.Relation,
        LinkId = linkId,
        Billable = true,
        DescriptionTemplate = $"{name} ({{date}})",
    };

    [Fact]
    public void Insert_then_GetAll_round_trips_every_field()
    {
        var store = Build();
        var id = store.Insert(new TaskMappingRule
        {
            TaskId = "42",
            TaskName = "Dev",
            LinkKind = CrmLinkKind.Project,
            LinkId = "p1",
            LinkName = "Website",
            Billable = false,
            DescriptionTemplate = "{task} ({date})",
        }, Now);

        var stored = Assert.Single(store.GetAll());
        Assert.Equal(id, stored.Id);
        Assert.Equal("42", stored.TaskId);
        Assert.Equal("Dev", stored.TaskName);
        Assert.Equal(CrmLinkKind.Project, stored.LinkKind);
        Assert.Equal("p1", stored.LinkId);
        Assert.Equal("Website", stored.LinkName);
        Assert.False(stored.Billable);
        Assert.Equal("{task} ({date})", stored.DescriptionTemplate);
        Assert.Equal(Now, stored.UpdatedAt);
    }

    [Fact]
    public void Blank_fields_are_stored_as_null_so_the_mapper_treats_them_as_unset()
    {
        var store = Build();
        store.Insert(new TaskMappingRule
        {
            TaskName = "Dev",
            LinkKind = CrmLinkKind.Task,
            LinkId = "t1",
            LinkName = "   ",
            DescriptionTemplate = "",
        }, Now);

        var stored = Assert.Single(store.GetAll());
        Assert.Null(stored.LinkName);
        Assert.Null(stored.DescriptionTemplate);
        Assert.Null(stored.Billable);
    }

    [Fact]
    public void Update_replaces_the_rule_but_keeps_its_position()
    {
        var store = Build();
        var first = store.Insert(Rule("A"), Now);
        store.Insert(Rule("B"), Now);

        Assert.True(store.Update(first, new TaskMappingRule
        {
            TaskName = "A renamed",
            LinkKind = CrmLinkKind.Project,
            LinkId = "p9",
        }, Now.AddHours(1)));

        var rules = store.GetAll();
        Assert.Equal(["A renamed", "B"], rules.Select(r => r.TaskName));
        Assert.Equal(CrmLinkKind.Project, rules[0].LinkKind);
        Assert.Equal("p9", rules[0].LinkId);
        Assert.Null(rules[0].Billable);
    }

    [Fact]
    public void Update_and_Delete_report_unknown_ids()
    {
        var store = Build();
        Assert.False(store.Update(999, Rule("A"), Now));
        Assert.False(store.Delete(999));
    }

    [Fact]
    public void Delete_removes_only_the_named_rule()
    {
        var store = Build();
        var a = store.Insert(Rule("A"), Now);
        store.Insert(Rule("B"), Now);

        Assert.True(store.Delete(a));
        Assert.Equal(["B"], store.GetAll().Select(r => r.TaskName));
    }

    [Fact]
    public void ToMappingConfig_preserves_insertion_order_because_the_first_match_wins()
    {
        var store = Build();
        store.Insert(Rule("A", linkId: "first"), Now);
        store.Insert(Rule("A", linkId: "second"), Now);

        var config = store.ToMappingConfig(skipUnmapped: false);

        Assert.False(config.SkipUnmapped);
        Assert.Equal("first", config.Resolve("", "A")!.LinkId);
    }

    [Fact]
    public void Initialize_is_idempotent_across_restarts()
    {
        var store = Build();
        store.Insert(Rule("A"), Now);

        var reopened = Build();
        Assert.Equal(["A"], reopened.GetAll().Select(r => r.TaskName));
    }

    [Fact]
    public void Sync_store_round_trips_a_split_entry()
    {
        var options = Options.Create(new SyncOptions { StateDbPath = _dbPath });
        var sync = new SyncStore(options);
        sync.Initialize();

        sync.Upsert(new SyncedEntry("555", ["a", "b"], "hash"), Now);

        var found = sync.Find("555")!;
        Assert.Equal(["a", "b"], found.CrmEntryIds);
        Assert.Equal("hash", found.ContentHash);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, $"{_dbPath}-wal", $"{_dbPath}-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}
