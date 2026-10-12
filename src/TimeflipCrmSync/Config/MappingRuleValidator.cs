using System.Text.RegularExpressions;

namespace TimeflipCrmSync.Config;

/// <summary>
/// Checks a rule submitted from the web UI before it reaches the database, so a
/// rule that could never produce a valid CRM time entry is rejected at the form
/// instead of failing during a sync pass.
/// </summary>
public static partial class MappingRuleValidator
{
    private static readonly string[] KnownPlaceholders = ["task", "date"];

    /// <summary>Returns one message per problem; empty means valid.</summary>
    public static IReadOnlyList<string> Validate(TaskMappingRule rule)
    {
        var errors = new List<string>();

        if (IsBlank(rule.TaskId) && IsBlank(rule.TaskName))
            errors.Add("Pick a TimeFlip task, or enter a task name to match on.");

        if (rule.LinkKind is null || IsBlank(rule.LinkId))
            errors.Add("Pick a CRM task, project or relation to link the time to.");
        else if (!Guid.TryParse(rule.LinkId, out _))
            errors.Add($"\"{rule.LinkId}\" is not a CRM id; pick the link from the list.");

        foreach (Match m in Placeholder().Matches(rule.DescriptionTemplate ?? ""))
        {
            var name = m.Groups[1].Value;
            if (!KnownPlaceholders.Contains(name, StringComparer.OrdinalIgnoreCase))
                errors.Add($"Unknown placeholder \"{{{name}}}\" in the description " +
                           $"template. Only {{task}} and {{date}} are supported.");
        }

        if ((rule.DescriptionTemplate?.Length ?? 0) > 1000)
            errors.Add("The description template can be at most 1000 characters.");

        return errors;
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    [GeneratedRegex(@"\{([^{}]*)\}")]
    private static partial Regex Placeholder();
}
