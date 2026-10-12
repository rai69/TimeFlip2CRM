using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Polly;
using TimeflipCrmSync;
using TimeflipCrmSync.Api;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Crm;
using TimeflipCrmSync.Dump;
using TimeflipCrmSync.Health;
using TimeflipCrmSync.Persistence;
using TimeflipCrmSync.Sync;
using TimeflipCrmSync.TimeFlip;

// Container HEALTHCHECK probe: GET /healthz and exit 0/1. Handled before any
// host/config setup so it needs no application configuration.
if (args.Contains("--healthcheck", StringComparer.OrdinalIgnoreCase))
    return await HealthProbe.RunAsync();

var builder = WebApplication.CreateBuilder(args);

// ---- Options (env vars use "__" as section separator, e.g. Crm__Token) -------
builder.Services.AddOptions<TimeFlipOptions>()
    .Bind(builder.Configuration.GetSection(TimeFlipOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddOptions<CrmOptions>()
    .Bind(builder.Configuration.GetSection(CrmOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(o => o.HasValidTimeZone(), "Crm:TimeZone is not a known time zone id (e.g. Europe/Amsterdam).")
    .ValidateOnStart();

builder.Services.AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection(SyncOptions.SectionName))
    .ValidateOnStart();

// ---- Infrastructure ----------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SyncStore>();
builder.Services.AddSingleton<MappingStore>();
builder.Services.AddSingleton<HealthState>();
builder.Services.AddSingleton<CrmLinkCache>();

// ---- Task mapping ------------------------------------------------------------
// Scoped, not singleton: SyncRunner builds a fresh scope for every pass, so rules
// edited in the web UI take effect on the next run without restarting the service.
builder.Services.AddScoped(sp =>
{
    var sync = sp.GetRequiredService<IOptions<SyncOptions>>().Value;
    return sp.GetRequiredService<MappingStore>().ToMappingConfig(sync.SkipUnmapped);
});

builder.Services.AddScoped(sp => new TimeEntryMapper(
    sp.GetRequiredService<MappingConfig>(),
    sp.GetRequiredService<IOptions<CrmOptions>>().Value));

// ---- HTTP clients with Polly retry/backoff -----------------------------------
builder.Services.AddHttpClient<ITimeFlipClient, TimeFlipClient>()
    .AddTransientHttpErrorPolicy(p =>
        p.WaitAndRetryAsync(3, n => TimeSpan.FromSeconds(Math.Pow(2, n))));

// The CRM allows 120 requests a minute per token and answers 429 with
// Retry-After above that; wait as told instead of failing the interval.
builder.Services.AddHttpClient<ICrmClient, CrmClient>()
    .AddTransientHttpErrorPolicy(p =>
        p.WaitAndRetryAsync(3, n => TimeSpan.FromSeconds(Math.Pow(2, n))))
    .AddPolicyHandler(Policy
        .HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(3,
            sleepDurationProvider: (n, outcome, _) => RetryAfter(outcome.Result, n),
            onRetryAsync: (outcome, _, _, _) =>
            {
                outcome.Result?.Dispose();
                return Task.CompletedTask;
            }));

builder.Services.AddScoped<SyncService>();
builder.Services.AddSingleton<SyncRunner>();
builder.Services.AddHostedService<Worker>();

// The UI sends and receives link kinds as "task" / "project" / "relation".
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddHealthChecks()
    .AddCheck<SyncHealthCheck>("sync");

var app = builder.Build();

// Create the schema before anything reads it: the UI, --dump and the worker all
// depend on it, and the worker is not guaranteed to have started first.
{
    app.Services.GetRequiredService<SyncStore>().Initialize();
    app.Services.GetRequiredService<MappingStore>().Initialize();
}

// Diagnostic mode: --dump signs in, prints intervals + mapping, and exits.
// --days N widens the window past the (short) scheduled lookback.
// The web server and worker are never started in this mode.
if (args.Contains("--dump", StringComparer.OrdinalIgnoreCase))
{
    int? dumpDays = null;
    var daysIndex = Array.FindIndex(args, a => a.Equals("--days", StringComparison.OrdinalIgnoreCase));
    if (daysIndex >= 0)
    {
        if (daysIndex + 1 >= args.Length || !int.TryParse(args[daysIndex + 1], out var parsed) || parsed < 1)
        {
            Console.Error.WriteLine("--days needs a positive number of days, e.g. --dump --days 30.");
            return 1;
        }
        dumpDays = parsed;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try
    {
        await DumpRunner.RunAsync(app.Services, dumpDays, cts.Token);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"\nDump failed: {ex.Message}");
        return 1;
    }
}

// Mapping UI (wwwroot/index.html) and the JSON endpoints behind it.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapMappingApi();

app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    ResponseWriter = WriteHealthJson,
});

// Manual trigger: run a sync pass now. Protected by SyncOptions.TriggerToken when set.
// ?lookbackDays=N re-scans further back than the scheduled passes do (which always
// use SyncOptions.LookbackDays); omit it to use the configured window.
// Returns 200 with the summary, 409 if a pass is already running, 401 on bad token,
// 400 on an out-of-range lookback.
app.MapPost("/sync", async (
    HttpRequest req,
    int? lookbackDays,
    SyncRunner runner,
    IOptions<SyncOptions> syncOptions,
    IHostApplicationLifetime lifetime) =>
{
    var options = syncOptions.Value;
    var expected = options.TriggerToken;
    if (!string.IsNullOrEmpty(expected) &&
        !string.Equals(req.Headers["X-Trigger-Token"], expected, StringComparison.Ordinal))
        return Results.Unauthorized();

    if (lookbackDays is { } days && (days < 1 || days > options.MaxManualLookbackDays))
        return Results.BadRequest(new
        {
            status = "invalid",
            message = $"lookbackDays must be between 1 and {options.MaxManualLookbackDays}.",
        });

    try
    {
        // Run to completion regardless of client disconnect; only shutdown cancels it.
        var summary = await runner.RunAsync(
            waitIfBusy: false, lookbackDays, lifetime.ApplicationStopping);
        return summary is null
            ? Results.Conflict(new { status = "busy", message = "A sync pass is already running." })
            : Results.Ok(new
            {
                status = "ok",
                lookbackDays = lookbackDays ?? options.LookbackDays,
                created = summary.Created,
                updated = summary.Updated,
                recreated = summary.Recreated,
                skipped = summary.Skipped,
                errors = summary.Errors,
            });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();
return 0;

// Wait as long as the CRM's Retry-After says (capped, so a bad header can't
// stall a pass), else back off exponentially.
static TimeSpan RetryAfter(HttpResponseMessage? response, int attempt)
{
    var max = TimeSpan.FromSeconds(60);
    var retryAfter = response?.Headers.RetryAfter;
    var wait = retryAfter?.Delta
               ?? (retryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : (TimeSpan?)null)
               ?? TimeSpan.FromSeconds(5 * Math.Pow(2, attempt - 1));
    return wait < TimeSpan.Zero ? TimeSpan.FromSeconds(1) : wait > max ? max : wait;
}

// Writes the health result (and the worker's last-sync detail) as JSON.
static Task WriteHealthJson(HttpContext ctx, HealthReport report)
{
    ctx.Response.ContentType = "application/json";

    var payload = new
    {
        status = report.Status.ToString(),
        totalDuration = report.TotalDuration.ToString(),
        entries = report.Entries.ToDictionary(
            e => e.Key,
            e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data,
            }),
    };

    return ctx.Response.WriteAsync(
        JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
}
