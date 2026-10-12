using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeflipCrmSync.Crm.Models;

/// <summary>
/// Body of POST /api/v1/time/entries and PUT /api/v1/time/entries/{id}.
/// Times are local to the organization's time zone; an end before the start
/// means the next day, and the CRM then splits the entry at midnight.
/// Null fields are left out, which the CRM reads as "use the default".
/// </summary>
public sealed class CrmTimeEntryRequest
{
    /// <summary>yyyy-MM-dd.</summary>
    public string Date { get; set; } = "";

    /// <summary>HH:mm.</summary>
    public string? StartTime { get; set; }

    /// <summary>HH:mm.</summary>
    public string? EndTime { get; set; }

    public string? Description { get; set; }

    /// <summary>Null: the default of the project or relation.</summary>
    public bool? Billable { get; set; }

    public string? TaskId { get; set; }
    public string? ProjectId { get; set; }
    public string? RelationId { get; set; }
}

public sealed class CrmTimeEntry
{
    public string Id { get; set; } = "";
    public string Date { get; set; } = "";
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    public int DurationMinutes { get; set; }
    public string? Description { get; set; }
    public bool Billable { get; set; }
    public CrmLinkRef? Task { get; set; }
    public CrmLinkRef? Project { get; set; }
    public CrmLinkRef? Relation { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CrmLinkRef
{
    public string Id { get; set; } = "";

    /// <summary>Null for someone else's private task.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// Answer to a create or update. Usually one entry; two when the times run past
/// midnight and the CRM split them (the first is the one created/updated).
/// </summary>
public sealed class CrmSavedTimeEntries
{
    public List<CrmTimeEntry> Entries { get; set; } = new();
}

public sealed class CrmTimeEntryPage
{
    public List<CrmTimeEntry> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>A task, project or relation from the /time/links lookups.</summary>
public sealed record CrmLink(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);

/// <summary>The CRM's application/problem+json error body.</summary>
public sealed class CrmProblem
{
    public string? Title { get; set; }
    public string? Detail { get; set; }
    public int? Status { get; set; }

    /// <summary>Stable error code, e.g. api.token_expired or validation_failed.</summary>
    public string? Code { get; set; }

    /// <summary>
    /// Per-field messages on validation_failed, normally { field: [messages] }.
    /// Kept raw so an unexpected shape can't hide the rest of the problem.
    /// </summary>
    public JsonElement? Errors { get; set; }

    /// <summary>The scopes that were missing on api.insufficient_scope.</summary>
    public string[]? RequiredScopes { get; set; }
}
