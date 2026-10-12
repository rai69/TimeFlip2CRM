using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.TimeFlip.Models;

namespace TimeflipCrmSync.TimeFlip;

public interface ITimeFlipClient
{
    Task<IReadOnlyList<TimeFlipInterval>> GetIntervalsAsync(
        DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>
    /// The tasks configured on the cube, for the mapping UI's task picker.
    /// </summary>
    Task<IReadOnlyList<TimeFlipTask>> GetTasksAsync(CancellationToken ct);
}

/// <summary>
/// Talks to the TimeFlip cloud API (https://newapi.timeflip.io, v2 spec).
///
/// Auth: POST /api/auth/email/sign-in { email, password } returns a JWT. The
/// spec does not pin down where the token is returned, so we look in the common
/// places (Authorization / Token response headers, then body fields) and send it
/// back as "Authorization: Bearer &lt;jwt&gt;".
///
/// Data: GET /api/sync/all returns SyncDataDto { tasks[], timeIntervals[] } — all
/// tasks and every tracked interval. It takes no date parameters, so we fetch the
/// full set and filter to the requested window client-side (cheap for a single
/// cube). Each DataInterval has startedAt + duration (seconds); end = start + dur.
/// delDate != null marks a deleted interval, which we drop.
/// </summary>
public sealed partial class TimeFlipClient : ITimeFlipClient
{
    private readonly HttpClient _http;
    private readonly TimeFlipOptions _options;
    private readonly ILogger<TimeFlipClient> _log;

    private string? _jwt;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TimeFlipClient(HttpClient http, IOptions<TimeFlipOptions> options,
        ILogger<TimeFlipClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (_jwt is not null)
        {
            ApplyToken();
            return;
        }

        var payload = new { email = _options.Email, password = _options.Password };
        using var resp = await _http.PostAsJsonAsync("api/auth/email/sign-in", payload, Json, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("TimeFlip sign-in failed: {Status} {Body}", (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }

        _jwt = ExtractToken(resp, await resp.Content.ReadAsStringAsync(ct));
        if (string.IsNullOrEmpty(_jwt))
            throw new InvalidOperationException(
                "TimeFlip sign-in succeeded but no JWT was found in the response " +
                "headers or body. Inspect the response and adjust ExtractToken.");

        ApplyToken();
        _log.LogInformation("Authenticated with TimeFlip.");
    }

    private void ApplyToken()
    {
        // TimeFlip's newapi expects the JWT back in a "Token" request header.
        // We also send it as a Bearer token so either convention works.
        _http.DefaultRequestHeaders.Remove("Token");
        _http.DefaultRequestHeaders.Add("Token", _jwt);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _jwt);
    }

    /// <summary>
    /// Pull the JWT out of the sign-in response. TimeFlip returns it in a
    /// response header named "token"; we also fall back to Authorization and
    /// the body for robustness.
    /// </summary>
    private static string? ExtractToken(HttpResponseMessage resp, string body)
    {
        // Primary: Token response header (name is case-insensitive).
        if (resp.Headers.TryGetValues("Token", out var tok))
        {
            var v = tok.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        // Fallback: Authorization: Bearer xxx (or a bare token).
        if (resp.Headers.TryGetValues("Authorization", out var auth))
        {
            var v = auth.FirstOrDefault();
            if (!string.IsNullOrEmpty(v))
                return v.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? v["Bearer ".Length..].Trim() : v.Trim();
        }
        // Fallback: a bare JWT string, or { token | accessToken | jwt: "..." } in the body.
        if (!string.IsNullOrWhiteSpace(body))
        {
            var trimmed = body.Trim();
            if (trimmed.StartsWith("eyJ", StringComparison.Ordinal))
                return trimmed.Trim('"');
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                foreach (var name in new[] { "token", "accessToken", "jwt", "access_token" })
                    if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                        doc.RootElement.TryGetProperty(name, out var el) &&
                        el.ValueKind == JsonValueKind.String)
                        return el.GetString();
            }
            catch (JsonException) { /* not JSON */ }
        }
        return null;
    }

    public async Task<IReadOnlyList<TimeFlipInterval>> GetIntervalsAsync(
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);

        var data = await GetSyncAllAsync(ct);
        if (data?.TimeIntervals is null || data.TimeIntervals.Count == 0)
            return Array.Empty<TimeFlipInterval>();

        var tasks = data.Tasks ?? new();
        // Intervals reference their task by taskLocalId (== task.localId); taskId is
        // often null. We index by both and resolve localId first, id second.
        var tasksByLocalId = tasks
            .Where(t => !string.IsNullOrEmpty(t.LocalId))
            .GroupBy(t => t.LocalId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var tasksById = tasks
            .Where(t => t.Id != 0)
            .GroupBy(t => t.Id)
            .ToDictionary(g => g.Key, g => g.First());

        var fromUtc = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var toUtc = to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var result = new List<TimeFlipInterval>();
        foreach (var iv in data.TimeIntervals)
        {
            if (!string.IsNullOrEmpty(iv.DelDate)) continue;      // deleted
            if (iv.Duration <= 0) continue;                       // running / empty
            if (!TryParseStart(iv.StartedAt, out var start)) continue;

            var end = start.AddSeconds(iv.Duration);
            if (end.UtcDateTime <= fromUtc || start.UtcDateTime >= toUtc) continue;

            DataTask? task = null;
            if (!string.IsNullOrEmpty(iv.TaskLocalId))
                tasksByLocalId.TryGetValue(iv.TaskLocalId, out task);
            if (task is null && iv.TaskId is { } tid)
                tasksById.TryGetValue(tid, out task);

            // Stable mapping key: server task id when known, else the local GUID.
            var taskKey = task is not null
                ? TaskKey(task)
                : iv.TaskLocalId ?? iv.TaskId?.ToString(CultureInfo.InvariantCulture) ?? "";

            result.Add(new TimeFlipInterval
            {
                Id = iv.Id.ToString(CultureInfo.InvariantCulture),
                TaskId = taskKey,
                TaskName = task?.Name ?? "(unnamed)",
                Start = start,
                End = end,
                Billable = task?.IsBillable,
            });
        }
        return result;
    }

    public async Task<IReadOnlyList<TimeFlipTask>> GetTasksAsync(CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct);

        var data = await GetSyncAllAsync(ct);
        if (data?.Tasks is null) return Array.Empty<TimeFlipTask>();

        return data.Tasks
            .Select(t => new TimeFlipTask(TaskKey(t), t.Name ?? "(unnamed)", t.IsBillable))
            .Where(t => t.Id.Length > 0)
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The key a mapping rule stores for a task. Must stay identical to the
    /// TaskId put on each interval in <see cref="GetIntervalsAsync"/>, or rules
    /// picked in the UI would silently never match.
    /// </summary>
    private static string TaskKey(DataTask task) =>
        task.Id != 0
            ? task.Id.ToString(CultureInfo.InvariantCulture)
            : task.LocalId ?? "";

    private async Task<SyncDataDto?> GetSyncAllAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("api/sync/all", ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _jwt = null;
            await EnsureAuthenticatedAsync(ct);
            using var retry = await _http.GetAsync("api/sync/all", ct);
            retry.EnsureSuccessStatusCode();
            return await retry.Content.ReadFromJsonAsync<SyncDataDto>(Json, ct);
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SyncDataDto>(Json, ct);
    }

    private static bool TryParseStart(string? value, out DateTimeOffset start)
    {
        start = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // TimeFlip emits offsets like "+02" (no minutes), which .NET won't parse.
        // Normalize a trailing "+HH" / "-HH" to "+HH:00".
        value = TrailingShortOffset().Replace(value.Trim(), "$1$2:00");

        // ISO 8601 with offset -> use as-is; without offset -> assume UTC.
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out start);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"([+-])(\d{2})$")]
    private static partial System.Text.RegularExpressions.Regex TrailingShortOffset();

    // ---- Raw API DTOs (v2 spec: SyncDataDto / DataTask / DataInterval) --------
    private sealed class SyncDataDto
    {
        public List<DataTask>? Tasks { get; set; }
        public List<DataInterval>? TimeIntervals { get; set; }
        public string? LastSyncedAt { get; set; }
    }

    private sealed class DataTask
    {
        public long Id { get; set; }
        public string? LocalId { get; set; }     // GUID; intervals join on this
        public string? Name { get; set; }
        public bool IsBillable { get; set; }
        public string? Tag { get; set; }
        public int SideIndex { get; set; }
    }

    private sealed class DataInterval
    {
        public long Id { get; set; }
        public long? TaskId { get; set; }        // often null; use TaskLocalId
        public string? TaskLocalId { get; set; }
        public string? StartedAt { get; set; }
        public int Duration { get; set; }        // seconds
        public string? DelDate { get; set; }     // non-null => deleted
    }
}
