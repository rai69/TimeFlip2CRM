using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Crm.Models;

namespace TimeflipCrmSync.Crm;

public interface ICrmClient
{
    /// <summary>Creates an entry; returns every entry it produced (two past midnight).</summary>
    Task<IReadOnlyList<CrmTimeEntry>> CreateTimeEntryAsync(
        CrmTimeEntryRequest request, CancellationToken ct);

    /// <summary>
    /// Replaces an entry; returns every entry it now covers (two past midnight).
    /// Throws <see cref="CrmApiException"/> with 404 when the entry is gone.
    /// </summary>
    Task<IReadOnlyList<CrmTimeEntry>> UpdateTimeEntryAsync(
        string id, CrmTimeEntryRequest request, CancellationToken ct);

    /// <summary>Deletes an entry. An entry that is already gone is not an error.</summary>
    Task DeleteTimeEntryAsync(string id, CancellationToken ct);

    /// <summary>The ids of all your own entries dated from..to (inclusive).</summary>
    Task<IReadOnlySet<string>> GetTimeEntryIdsAsync(
        DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Open tasks, projects or relations whose name contains the search text.</summary>
    Task<IReadOnlyList<CrmLink>> LookupLinksAsync(
        CrmLinkKind kind, string? search, CancellationToken ct);
}

/// <summary>An error answer from the CRM, with its stable problem code.</summary>
public sealed class CrmApiException(HttpStatusCode status, string? code, string message)
    : HttpRequestException(message, null, status)
{
    public string? Code { get; } = code;
}

/// <summary>
/// Client for the CRM's external API (https://crm.green-cloud.nl/apidocs/).
///
/// Auth is a personal API token sent as a bearer token; it only reaches the
/// token owner's own hours, so entries are always written as that user.
/// The CRM speaks camelCase JSON, local dates (yyyy-MM-dd) and local times
/// (HH:mm) in the organization's time zone. Errors are problem+json with a
/// stable <c>code</c>, surfaced through <see cref="CrmApiException"/>.
/// 429s are retried with Retry-After by the Polly policy in Program.cs.
/// </summary>
public sealed class CrmClient : ICrmClient
{
    private const int PageSize = 200;      // API maximum
    private const int MaxPages = 50;       // 10 000 entries: far beyond any sync window
    private const int LookupLimit = 100;   // API maximum

    private readonly HttpClient _http;
    private readonly ILogger<CrmClient> _log;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CrmClient(HttpClient http, IOptions<CrmOptions> options, ILogger<CrmClient> log)
    {
        _http = http;
        _log = log;

        _http.BaseAddress = new Uri(options.Value.BaseUrl.TrimEnd('/') + "/api/v1/");
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.Value.Token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<CrmTimeEntry>> CreateTimeEntryAsync(
        CrmTimeEntryRequest request, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("time/entries", request, Json, ct);
        await EnsureSuccess(resp, "create time entry", ct);
        return await ReadSavedAsync(resp, "creating", ct);
    }

    public async Task<IReadOnlyList<CrmTimeEntry>> UpdateTimeEntryAsync(
        string id, CrmTimeEntryRequest request, CancellationToken ct)
    {
        using var resp = await _http.PutAsJsonAsync(
            $"time/entries/{Uri.EscapeDataString(id)}", request, Json, ct);
        await EnsureSuccess(resp, "update time entry", ct);
        return await ReadSavedAsync(resp, "updating", ct);
    }

    public async Task DeleteTimeEntryAsync(string id, CancellationToken ct)
    {
        using var resp = await _http.DeleteAsync($"time/entries/{Uri.EscapeDataString(id)}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccess(resp, "delete time entry", ct);
    }

    public async Task<IReadOnlySet<string>> GetTimeEntryIdsAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var range = $"from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";

        for (var page = 1; page <= MaxPages; page++)
        {
            using var resp = await _http.GetAsync(
                $"time/entries?{range}&page={page}&pageSize={PageSize}", ct);
            await EnsureSuccess(resp, "list time entries", ct);

            var result = await resp.Content.ReadFromJsonAsync<CrmTimeEntryPage>(Json, ct)
                         ?? throw new InvalidOperationException("Empty response listing time entries.");
            foreach (var entry in result.Items) ids.Add(entry.Id);

            if (result.Items.Count == 0 || (long)page * PageSize >= result.TotalCount) return ids;
        }

        // Returning a partial set would make the sync think entries were deleted
        // and recreate them as duplicates, so refuse instead.
        throw new InvalidOperationException(
            $"More than {MaxPages * PageSize} CRM entries between {from} and {to}; " +
            "narrow the sync window.");
    }

    public async Task<IReadOnlyList<CrmLink>> LookupLinksAsync(
        CrmLinkKind kind, string? search, CancellationToken ct)
    {
        var path = kind switch
        {
            CrmLinkKind.Task => "time/links/tasks",
            CrmLinkKind.Project => "time/links/projects",
            CrmLinkKind.Relation => "time/links/relations",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        var query = string.Create(CultureInfo.InvariantCulture, $"limit={LookupLimit}");
        if (!string.IsNullOrWhiteSpace(search))
            query += "&search=" + Uri.EscapeDataString(search.Trim());

        using var resp = await _http.GetAsync($"{path}?{query}", ct);
        await EnsureSuccess(resp, $"look up {kind.ToString().ToLowerInvariant()}s", ct);
        return await resp.Content.ReadFromJsonAsync<List<CrmLink>>(Json, ct) ?? [];
    }

    private static async Task<IReadOnlyList<CrmTimeEntry>> ReadSavedAsync(
        HttpResponseMessage resp, string action, CancellationToken ct)
    {
        var saved = await resp.Content.ReadFromJsonAsync<CrmSavedTimeEntries>(Json, ct);
        if (saved is null || saved.Entries.Count == 0)
            throw new InvalidOperationException($"The CRM returned no entries {action} a time entry.");
        return saved.Entries;
    }

    private async Task EnsureSuccess(HttpResponseMessage resp, string action, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync(ct);
        _log.LogError("CRM {Action} failed: {Status} {Body}", action, (int)resp.StatusCode, body);

        CrmProblem? problem = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
                problem = JsonSerializer.Deserialize<CrmProblem>(body, Json);
        }
        catch (JsonException) { /* not problem+json, e.g. a proxy error page */ }

        throw new CrmApiException(resp.StatusCode, problem?.Code,
            $"CRM {action} failed ({(int)resp.StatusCode}): {Describe(problem, resp)}");
    }

    private static string Describe(CrmProblem? problem, HttpResponseMessage resp)
    {
        if (problem is null) return resp.ReasonPhrase ?? "no details";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(problem.Title)) parts.Add(problem.Title.Trim());
        if (!string.IsNullOrWhiteSpace(problem.Detail)) parts.Add(problem.Detail.Trim());
        if (problem.Errors is { ValueKind: JsonValueKind.Object } errors)
            parts.Add(string.Join("; ", errors.EnumerateObject().Select(e => $"{e.Name}: {Flatten(e.Value)}")));
        if (problem.RequiredScopes is { Length: > 0 })
            parts.Add("token needs scope " + string.Join(", ", problem.RequiredScopes));
        if (!string.IsNullOrWhiteSpace(problem.Code)) parts.Add($"[{problem.Code}]");

        return parts.Count > 0 ? string.Join(" ", parts) : resp.ReasonPhrase ?? "no details";
    }

    private static string Flatten(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Flatten)),
        JsonValueKind.String => value.GetString() ?? "",
        _ => value.GetRawText(),
    };
}
