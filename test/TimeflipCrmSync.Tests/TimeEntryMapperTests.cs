using TimeflipCrmSync.Config;
using TimeflipCrmSync.Sync;
using TimeflipCrmSync.TimeFlip.Models;
using Xunit;

namespace TimeflipCrmSync.Tests;

public class TimeEntryMapperTests
{
    private const string TaskGuid = "0199a3f0-1b2c-7d3e-8f40-5a6b7c8d9e01";

    // 09:00–10:30 UTC on a summer day: 11:00–12:30 in Amsterdam (CEST).
    private static TimeFlipInterval Interval(
        string taskId = "42", string taskName = "Dev",
        DateTimeOffset? start = null, DateTimeOffset? end = null) => new()
    {
        Id = "1",
        TaskId = taskId,
        TaskName = taskName,
        Start = start ?? new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
        End = end ?? new DateTimeOffset(2026, 7, 20, 10, 30, 0, TimeSpan.Zero),
    };

    private static TaskMappingRule Rule(CrmLinkKind kind = CrmLinkKind.Task, string? template = null,
        bool? billable = null) => new()
    {
        TaskId = "42",
        LinkKind = kind,
        LinkId = TaskGuid,
        Billable = billable,
        DescriptionTemplate = template,
    };

    private static TimeEntryMapper Mapper(params TaskMappingRule[] rules) =>
        new(new MappingConfig { Rules = rules.ToList() }, new CrmOptions());

    [Fact]
    public void Unmapped_task_returns_null_when_skip_enabled()
    {
        var mapper = new TimeEntryMapper(new MappingConfig { SkipUnmapped = true }, new CrmOptions());
        Assert.Null(mapper.Map(Interval()));
    }

    [Fact]
    public void Unmapped_task_throws_when_skip_disabled()
    {
        var mapper = new TimeEntryMapper(new MappingConfig { SkipUnmapped = false }, new CrmOptions());
        Assert.Throws<InvalidOperationException>(() => mapper.Map(Interval()));
    }

    [Fact]
    public void Rule_matched_by_taskId_takes_precedence_over_name()
    {
        var byName = new TaskMappingRule { TaskName = "Dev", LinkKind = CrmLinkKind.Relation, LinkId = "by-name" };
        var byId = new TaskMappingRule { TaskId = "42", LinkKind = CrmLinkKind.Relation, LinkId = "by-id" };

        var result = Mapper(byName, byId).Map(Interval(taskId: "42", taskName: "Dev"));

        Assert.Equal("by-id", result!.Request.RelationId);
    }

    [Fact]
    public void Rule_matched_by_name_is_case_insensitive()
    {
        var rule = new TaskMappingRule { TaskName = "dev", LinkKind = CrmLinkKind.Project, LinkId = "p1" };
        var result = Mapper(rule).Map(Interval(taskName: "Dev"));
        Assert.Equal("p1", result!.Request.ProjectId);
    }

    [Theory]
    [InlineData(CrmLinkKind.Task)]
    [InlineData(CrmLinkKind.Project)]
    [InlineData(CrmLinkKind.Relation)]
    public void Only_the_chosen_link_is_sent(CrmLinkKind kind)
    {
        var r = Mapper(Rule(kind)).Map(Interval())!.Request;

        Assert.Equal(kind == CrmLinkKind.Task ? TaskGuid : null, r.TaskId);
        Assert.Equal(kind == CrmLinkKind.Project ? TaskGuid : null, r.ProjectId);
        Assert.Equal(kind == CrmLinkKind.Relation ? TaskGuid : null, r.RelationId);
    }

    [Fact]
    public void Billable_is_left_to_the_crm_unless_the_rule_sets_it()
    {
        Assert.Null(Mapper(Rule()).Map(Interval())!.Request.Billable);
        Assert.False(Mapper(Rule(billable: false)).Map(Interval())!.Request.Billable);
        Assert.True(Mapper(Rule(billable: true)).Map(Interval())!.Request.Billable);
    }

    [Fact]
    public void Times_are_sent_as_local_date_and_clock_times()
    {
        var r = Mapper(Rule()).Map(Interval())!.Request;

        Assert.Equal("2026-07-20", r.Date);
        Assert.Equal("11:00", r.StartTime);
        Assert.Equal("12:30", r.EndTime);
    }

    [Fact]
    public void Winter_time_uses_the_winter_offset()
    {
        var r = Mapper(Rule()).Map(Interval(
            start: new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero),
            end: new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero)))!.Request;

        Assert.Equal("10:00", r.StartTime);
        Assert.Equal("11:00", r.EndTime);
    }

    [Fact]
    public void The_date_is_the_local_day_even_when_utc_is_still_the_day_before()
    {
        // 22:30 UTC on the 19th is 00:30 on the 20th in Amsterdam.
        var r = Mapper(Rule()).Map(Interval(
            start: new DateTimeOffset(2026, 7, 19, 22, 30, 0, TimeSpan.Zero),
            end: new DateTimeOffset(2026, 7, 19, 23, 30, 0, TimeSpan.Zero)))!.Request;

        Assert.Equal("2026-07-20", r.Date);
        Assert.Equal("00:30", r.StartTime);
    }

    [Fact]
    public void A_session_past_midnight_is_sent_with_the_end_before_the_start()
    {
        var r = Mapper(Rule()).Map(Interval(
            start: new DateTimeOffset(2026, 7, 19, 21, 30, 0, TimeSpan.Zero),
            end: new DateTimeOffset(2026, 7, 19, 22, 30, 0, TimeSpan.Zero)))!.Request;

        Assert.Equal("2026-07-19", r.Date);
        Assert.Equal("23:30", r.StartTime);
        Assert.Equal("00:30", r.EndTime);
    }

    [Fact]
    public void Times_round_to_the_nearest_minute()
    {
        var r = Mapper(Rule()).Map(Interval(
            start: new DateTimeOffset(2026, 7, 20, 9, 0, 29, TimeSpan.Zero),
            end: new DateTimeOffset(2026, 7, 20, 9, 45, 30, TimeSpan.Zero)))!.Request;

        Assert.Equal("11:00", r.StartTime);
        Assert.Equal("11:46", r.EndTime);
    }

    [Fact]
    public void A_one_minute_session_never_rounds_to_zero()
    {
        for (var offset = 0; offset < 60; offset++)
        {
            var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero).AddSeconds(offset);
            var r = Mapper(Rule()).Map(Interval(start: start, end: start.AddSeconds(60)))!.Request;
            Assert.NotEqual(r.StartTime, r.EndTime);
        }
    }

    [Fact]
    public void A_session_of_24_hours_or_more_is_rejected()
    {
        var start = new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Mapper(Rule()).Map(Interval(start: start, end: start.AddHours(24))));
        Assert.Contains("24 h", ex.Message);
    }

    [Fact]
    public void Description_template_substitutes_task_and_local_date()
    {
        var result = Mapper(Rule(template: "{task} on {date}")).Map(Interval(taskName: "Dev"));
        Assert.Equal("Dev on 2026-07-20", result!.Request.Description);
    }

    [Fact]
    public void Description_defaults_to_task_name()
    {
        var result = Mapper(Rule()).Map(Interval(taskName: "Admin work"));
        Assert.Equal("Admin work", result!.Request.Description);
    }

    [Fact]
    public void Description_is_cut_to_the_crm_maximum()
    {
        var result = Mapper(Rule()).Map(Interval(taskName: new string('x', 1200)));
        Assert.Equal(1000, result!.Request.Description!.Length);
    }
}
