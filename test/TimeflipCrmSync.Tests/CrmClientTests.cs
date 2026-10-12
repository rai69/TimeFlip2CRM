using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeflipCrmSync.Config;
using TimeflipCrmSync.Crm;
using TimeflipCrmSync.Crm.Models;
using Xunit;

namespace TimeflipCrmSync.Tests;

public sealed class CrmClientTests
{
    /// <summary>Answers each request from a queue and records what was sent.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public Queue<HttpResponseMessage> Responses { get; } = new();
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return Responses.Dequeue();
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string type = "application/json") =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, type) };

    private static (CrmClient Client, StubHandler Handler) Build()
    {
        var handler = new StubHandler();
        var client = new CrmClient(
            new HttpClient(handler),
            Options.Create(new CrmOptions { BaseUrl = "https://crm.example/", Token = "crm_pat_x" }),
            NullLogger<CrmClient>.Instance);
        return (client, handler);
    }

    private const string Saved = """
        { "entries": [
            { "id": "e1", "date": "2026-10-05", "startTime": "23:30:00", "endTime": "00:00:00",
              "durationMinutes": 30, "description": "Dev", "billable": true,
              "task": null, "project": { "id": "p1", "name": "Website" }, "relation": null,
              "updatedAt": "2026-10-05T08:30:12Z" },
            { "id": "e2", "date": "2026-10-06", "startTime": "00:00:00", "endTime": "00:30:00",
              "durationMinutes": 30, "description": "Dev", "billable": true,
              "task": null, "project": null, "relation": null, "updatedAt": "2026-10-05T08:30:12Z" }
        ] }
        """;

    [Fact]
    public async Task Create_posts_camelCase_json_without_nulls_and_returns_every_entry()
    {
        var (client, handler) = Build();
        handler.Responses.Enqueue(Json(HttpStatusCode.Created, Saved));

        var entries = await client.CreateTimeEntryAsync(new CrmTimeEntryRequest
        {
            Date = "2026-10-05",
            StartTime = "23:30",
            EndTime = "00:30",
            Description = "Dev",
            ProjectId = "p1",
        }, CancellationToken.None);

        Assert.Equal(["e1", "e2"], entries.Select(e => e.Id));

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://crm.example/api/v1/time/entries", request.RequestUri!.ToString());
        Assert.Equal("Bearer crm_pat_x", request.Headers.Authorization!.ToString());

        using var doc = JsonDocument.Parse(body!);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["date", "startTime", "endTime", "description", "projectId"], names);
    }

    [Fact]
    public async Task Problem_details_become_a_readable_exception_with_the_code()
    {
        var (client, handler) = Build();
        handler.Responses.Enqueue(Json(HttpStatusCode.BadRequest, """
            { "title": "One or more fields are invalid.", "status": 400,
              "code": "validation_failed",
              "errors": { "endTime": ["The end time is not valid."] } }
            """, "application/problem+json"));

        var ex = await Assert.ThrowsAsync<CrmApiException>(() =>
            client.CreateTimeEntryAsync(new CrmTimeEntryRequest { Date = "2026-10-05" }, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("validation_failed", ex.Code);
        Assert.Contains("endTime: The end time is not valid.", ex.Message);
    }

    [Fact]
    public async Task A_missing_scope_names_the_scope_needed()
    {
        var (client, handler) = Build();
        handler.Responses.Enqueue(Json(HttpStatusCode.Forbidden, """
            { "title": "Forbidden", "status": 403, "code": "api.insufficient_scope",
              "requiredScopes": ["links.read"] }
            """, "application/problem+json"));

        var ex = await Assert.ThrowsAsync<CrmApiException>(() =>
            client.LookupLinksAsync(CrmLinkKind.Task, null, CancellationToken.None));

        Assert.Contains("links.read", ex.Message);
    }

    [Fact]
    public async Task Deleting_an_entry_that_is_already_gone_is_fine()
    {
        var (client, handler) = Build();
        handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        await client.DeleteTimeEntryAsync("e1", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Request.Method);
    }

    [Fact]
    public async Task Listing_ids_walks_every_page()
    {
        var (client, handler) = Build();
        var page1 = string.Join(",", Enumerable.Range(1, 200).Select(i => $$"""{ "id": "e{{i}}" }"""));
        handler.Responses.Enqueue(Json(HttpStatusCode.OK,
            $$"""{ "items": [{{page1}}], "page": 1, "pageSize": 200, "totalCount": 201 }"""));
        handler.Responses.Enqueue(Json(HttpStatusCode.OK,
            """{ "items": [ { "id": "e201" } ], "page": 2, "pageSize": 200, "totalCount": 201 }"""));

        var ids = await client.GetTimeEntryIdsAsync(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 5), CancellationToken.None);

        Assert.Equal(201, ids.Count);
        Assert.Contains("e201", ids);
        Assert.Equal(
            "https://crm.example/api/v1/time/entries?from=2026-10-01&to=2026-10-05&page=2&pageSize=200",
            handler.Requests[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Link_lookups_search_by_name_with_the_maximum_limit()
    {
        var (client, handler) = Build();
        handler.Responses.Enqueue(Json(HttpStatusCode.OK, """[ { "id": "r1", "name": "Jansen Bouw B.V." } ]"""));

        var links = await client.LookupLinksAsync(CrmLinkKind.Relation, " jansen bouw ", CancellationToken.None);

        Assert.Equal("Jansen Bouw B.V.", Assert.Single(links).Name);
        Assert.Equal("https://crm.example/api/v1/time/links/relations?limit=100&search=jansen%20bouw",
            handler.Requests[0].Request.RequestUri!.AbsoluteUri);
    }
}
