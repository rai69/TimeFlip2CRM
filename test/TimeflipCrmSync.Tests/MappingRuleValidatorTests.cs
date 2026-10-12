using TimeflipCrmSync.Config;
using Xunit;

namespace TimeflipCrmSync.Tests;

public sealed class MappingRuleValidatorTests
{
    private static TaskMappingRule Valid() => new()
    {
        TaskId = "42",
        LinkKind = CrmLinkKind.Project,
        LinkId = "0199a3ef-aa11-7b22-8c33-d4e5f6a7b802",
        LinkName = "Website",
        DescriptionTemplate = "{task} ({date})",
    };

    [Fact]
    public void A_complete_rule_passes()
    {
        Assert.Empty(MappingRuleValidator.Validate(Valid()));
    }

    [Fact]
    public void A_task_name_alone_identifies_the_task()
    {
        var rule = Valid();
        rule.TaskId = null;
        rule.TaskName = "Dev";

        Assert.Empty(MappingRuleValidator.Validate(rule));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_rule_with_no_task_is_rejected(string? taskName)
    {
        var rule = Valid();
        rule.TaskId = null;
        rule.TaskName = taskName;

        Assert.Contains(MappingRuleValidator.Validate(rule), e => e.Contains("TimeFlip task"));
    }

    [Fact]
    public void A_rule_without_a_crm_link_is_rejected()
    {
        var rule = Valid();
        rule.LinkId = "  ";

        Assert.Contains(MappingRuleValidator.Validate(rule), e => e.Contains("CRM task, project or relation"));
    }

    [Fact]
    public void A_link_id_without_a_kind_is_rejected()
    {
        var rule = Valid();
        rule.LinkKind = null;

        Assert.Single(MappingRuleValidator.Validate(rule));
    }

    [Fact]
    public void A_link_id_that_is_not_a_crm_id_is_rejected()
    {
        var rule = Valid();
        rule.LinkId = "Website";

        Assert.Contains(MappingRuleValidator.Validate(rule), e => e.Contains("not a CRM id"));
    }

    [Fact]
    public void An_unknown_placeholder_is_rejected_because_it_would_survive_into_the_description()
    {
        var rule = Valid();
        rule.DescriptionTemplate = "{task} for {client}";

        var error = Assert.Single(MappingRuleValidator.Validate(rule));
        Assert.Contains("{client}", error);
    }

    [Fact]
    public void An_empty_template_is_allowed_and_falls_back_to_the_task_name()
    {
        var rule = Valid();
        rule.DescriptionTemplate = null;

        Assert.Empty(MappingRuleValidator.Validate(rule));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var errors = MappingRuleValidator.Validate(new TaskMappingRule
        {
            DescriptionTemplate = "{nope}",
        });

        Assert.Equal(3, errors.Count);
    }
}
