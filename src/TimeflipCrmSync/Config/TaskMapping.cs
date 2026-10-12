namespace TimeflipCrmSync.Config;

/// <summary>What a mapping rule links the time to in the CRM.</summary>
public enum CrmLinkKind
{
    Task,
    Project,
    Relation,
}

/// <summary>
/// One mapping rule: a TimeFlip task (matched by id or name) to a single CRM
/// task, project or relation, plus optional overrides.
///
/// One link is always enough: the CRM fills in the rest itself (a task brings
/// its project and relation, a project brings its relation), and sending two
/// that disagree would be rejected.
/// </summary>
public sealed class TaskMappingRule
{
    /// <summary>TimeFlip task id to match. Takes precedence over <see cref="TaskName"/>.</summary>
    public string? TaskId { get; set; }

    /// <summary>TimeFlip task name to match (case-insensitive) when no id is given.</summary>
    public string? TaskName { get; set; }

    public CrmLinkKind? LinkKind { get; set; }

    /// <summary>The CRM id (uuid) of the linked task, project or relation.</summary>
    public string? LinkId { get; set; }

    /// <summary>
    /// The CRM name at the time the rule was saved. Display only: the CRM has no
    /// lookup by id, so this keeps the rules table readable.
    /// </summary>
    public string? LinkName { get; set; }

    /// <summary>Override billable for this task; null leaves it to the CRM's default.</summary>
    public bool? Billable { get; set; }

    /// <summary>
    /// Optional description template. Placeholders: {task}, {date}.
    /// Defaults to the task name when omitted.
    /// </summary>
    public string? DescriptionTemplate { get; set; }
}

public sealed class MappingConfig
{
    public List<TaskMappingRule> Rules { get; set; } = new();

    /// <summary>When true, tasks with no matching rule are skipped instead of erroring.</summary>
    public bool SkipUnmapped { get; set; } = true;

    public TaskMappingRule? Resolve(string taskId, string taskName)
    {
        return Rules.FirstOrDefault(r =>
                   !string.IsNullOrEmpty(r.TaskId) &&
                   string.Equals(r.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
               ?? Rules.FirstOrDefault(r =>
                   !string.IsNullOrEmpty(r.TaskName) &&
                   string.Equals(r.TaskName, taskName, StringComparison.OrdinalIgnoreCase));
    }
}
