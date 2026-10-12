using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Crm;
using TimeflipCrmSync.Health;
using TimeflipCrmSync.Persistence;
using TimeflipCrmSync.Sync;
using TimeflipCrmSync.TimeFlip;

namespace TimeflipCrmSync.Api;

/// <summary>What the browser sends when adding or editing a rule.</summary>
public sealed record RuleInput(
    string? TaskId,
    string? TaskName,
    CrmLinkKind? LinkKind,
    string? LinkId,
    string? LinkName,
    bool? Billable,
    string? DescriptionTemplate)
{
    public TaskMappingRule ToRule() => new()
    {
        TaskId = TaskId,
        TaskName = TaskName,
        LinkKind = LinkKind,
        LinkId = LinkId,
        LinkName = LinkName,
        Billable = Billable,
        DescriptionTemplate = DescriptionTemplate,
    };
}

/// <summary>A stored rule as returned to the browser.</summary>
public sealed record RuleDto(
    long Id,
    string? TaskId,
    string? TaskName,
    CrmLinkKind? LinkKind,
    string? LinkId,
    string? LinkName,
    bool? Billable,
    string? DescriptionTemplate,
    DateTimeOffset UpdatedAt)
{
    public static RuleDto From(MappingRuleRecord r) => new(
        r.Id, r.TaskId, r.TaskName, r.LinkKind, r.LinkId, r.LinkName,
        r.Billable, r.DescriptionTemplate, r.UpdatedAt);
}

/// <summary>
/// JSON endpoints behind the mapping UI in wwwroot. Unauthenticated, like
/// POST /sync — the service is expected to sit on a trusted LAN.
/// </summary>
public static class MappingApi
{
    public static void MapMappingApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---- Mapping rules ---------------------------------------------------

        api.MapGet("/rules", (MappingStore store) =>
            store.GetAll().Select(RuleDto.From));

        api.MapPost("/rules", (RuleInput input, MappingStore store, TimeProvider clock) =>
        {
            var rule = input.ToRule();
            var errors = MappingRuleValidator.Validate(rule);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            var id = store.Insert(rule, clock.GetUtcNow());
            var created = store.Get(id)!;
            return Results.Created($"/api/rules/{id}", RuleDto.From(created));
        });

        api.MapPut("/rules/{id:long}", (long id, RuleInput input,
            MappingStore store, TimeProvider clock) =>
        {
            var rule = input.ToRule();
            var errors = MappingRuleValidator.Validate(rule);
            if (errors.Count > 0) return Results.BadRequest(new { errors });

            if (!store.Update(id, rule, clock.GetUtcNow())) return Results.NotFound();
            return Results.Ok(RuleDto.From(store.Get(id)!));
        });

        api.MapDelete("/rules/{id:long}", (long id, MappingStore store) =>
            store.Delete(id) ? Results.NoContent() : Results.NotFound());

        // ---- Pickers ---------------------------------------------------------
        // Upstream failures come back as 502 with the reason, so the UI can say
        // why a dropdown is empty instead of showing nothing.

        api.MapGet("/timeflip/tasks", async (ITimeFlipClient timeflip, CancellationToken ct) =>
            await Upstream("TimeFlip", () => timeflip.GetTasksAsync(ct)));

        // {kind} is task, project or relation; ?search= narrows by name (the CRM
        // returns at most 100), ?refresh=true bypasses the 5-minute cache.
        api.MapGet("/crm/links/{kind}", async (string kind, string? search, bool? refresh,
            ICrmClient crm, CrmLinkCache cache, CancellationToken ct) =>
        {
            if (!Enum.TryParse<CrmLinkKind>(kind, ignoreCase: true, out var linkKind) ||
                !Enum.IsDefined(linkKind))
                return Results.NotFound();

            return await Upstream("the CRM", async () =>
                (await cache.LookupAsync(crm, linkKind, search, refresh ?? false, ct))
                    .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList());
        });

        // ---- Status ----------------------------------------------------------

        api.MapGet("/status", (HealthState health, SyncRunner runner,
            IOptions<SyncOptions> options) =>
        {
            var s = health.Snapshot();
            var o = options.Value;
            return Results.Ok(new
            {
                startedAt = s.StartedAt,
                lastRunAt = s.LastRunAt,
                lastSuccessAt = s.LastSuccessAt,
                lastError = s.LastError,
                isRunning = runner.IsRunning,
                lastSummary = s.LastSummary is { } sum
                    ? new
                    {
                        created = sum.Created,
                        updated = sum.Updated,
                        recreated = sum.Recreated,
                        skipped = sum.Skipped,
                        errors = sum.Errors,
                    }
                    : null,
                config = new
                {
                    intervalMinutes = o.IntervalMinutes,
                    lookbackDays = o.LookbackDays,
                    maxManualLookbackDays = o.MaxManualLookbackDays,
                    dryRun = o.DryRun,
                    skipUnmapped = o.SkipUnmapped,
                    triggerTokenRequired = !string.IsNullOrEmpty(o.TriggerToken),
                },
            });
        });
    }

    private static async Task<IResult> Upstream<T>(string service, Func<Task<T>> fetch)
    {
        try
        {
            return Results.Ok(await fetch());
        }
        catch (Exception ex)
        {
            return Results.Problem(
                title: $"Could not reach {service}.",
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
