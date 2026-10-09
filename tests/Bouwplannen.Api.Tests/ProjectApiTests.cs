using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Bouwplannen.Api.Tests;

public static class ProjectApiTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All() =>
    [
        ("health is open without a key", HealthIsOpen),
        ("project routes require the API key", RequiresApiKey),
        ("no key configured means no check", NoKeyConfigured),
        ("create, read back, items stored unchanged", CreateAndRead),
        ("update with If-Match, conflict on a stale version", OptimisticConcurrency),
        ("If-Match: * on a missing project is 404", IfMatchStarOnMissing),
        ("invalid input gives validation errors", Validation),
        ("malformed If-Match is a 400", MalformedIfMatch),
        ("since cursor returns only changes, deletions as tombstones", SinceCursor),
        ("delete with a stale version is a conflict", DeleteConflict),
        ("a deleted project can be stored again", RecreateAfterDelete),
        ("data survives a restart", Persistence),
        ("a damaged file is skipped, the rest still loads", DamagedFileSkipped),
        ("parallel writes lose no versions", ParallelWrites),
        ("oversized body is rejected", TooLarge),
        ("CORS allows only configured origins and exposes ETag", Cors),
    ];

    private static async Task WithHost(Func<TestHost, Task> test, string? apiKey = TestHost.ApiKey)
    {
        var host = await TestHost.StartAsync(apiKey: apiKey);
        try
        {
            await test(host);
        }
        finally
        {
            await host.DisposeAsync();
            TestHost.DeleteFolder(host.DataDirectory);
        }
    }

    private static Task HealthIsOpen() => WithHost(async h =>
    {
        using var anon = h.ClientWithoutKey();
        var r = await anon.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", (await TestHost.ReadJson(r)).GetProperty("status").GetString());
    });

    private static Task RequiresApiKey() => WithHost(async h =>
    {
        using var anon = h.ClientWithoutKey();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/projects")).StatusCode);
        anon.DefaultRequestHeaders.Add("X-Api-Key", "fout");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/projects/p1", TestHost.Project("p1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync("/api/projects")).StatusCode);
    });

    private static Task NoKeyConfigured() => WithHost(async h =>
    {
        using var anon = h.ClientWithoutKey();
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/projects")).StatusCode);
    }, apiKey: "");

    private static Task CreateAndRead() => WithHost(async h =>
    {
        var put = await h.Client.PutAsJsonAsync("/api/projects/p1", TestHost.Project("p1"));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal("\"1\"", put.Headers.ETag?.Tag);
        var created = await TestHost.ReadJson(put);
        Assert.Equal(1, created.GetProperty("version").GetInt64());
        Assert.False(created.GetProperty("deleted").GetBoolean());

        var get = await h.Client.GetAsync("/api/projects/p1");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var doc = await TestHost.ReadJson(get);
        Assert.Equal("Schuur", doc.GetProperty("name").GetString());
        // Timestamps come back exactly as sent, so the app can compare them as text.
        Assert.Equal("2026-10-09T10:00:00.000Z", doc.GetProperty("updated").GetString());
        var item = doc.GetProperty("items")[0];
        Assert.Equal("1,414", item.GetProperty("c").GetString());

        var list = await TestHost.ReadJson(await h.Client.GetAsync("/api/projects"));
        Assert.Equal(1, list.GetProperty("projects").GetArrayLength());
        Assert.True(list.TryGetProperty("serverTime", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/api/projects/onbekend")).StatusCode);
    });

    private static async Task<HttpResponseMessage> Put(HttpClient c, string id, object body, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{id}") { Content = JsonContent.Create(body) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> Delete(HttpClient c, string id, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{id}");
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static Task OptimisticConcurrency() => WithHost(async h =>
    {
        await Put(h.Client, "p1", TestHost.Project("p1"));
        var ok = await Put(h.Client, "p1", TestHost.Project("p1", "Schuur v2"), "\"1\"");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, (await TestHost.ReadJson(ok)).GetProperty("version").GetInt64());

        var stale = await Put(h.Client, "p1", TestHost.Project("p1", "Schuur van ander apparaat"), "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await TestHost.ReadJson(stale);
        Assert.Equal("Schuur v2", current.GetProperty("name").GetString());
        Assert.Equal(2, current.GetProperty("version").GetInt64());

        // Unconditional write still works (last writer wins when the client chooses that).
        Assert.Equal(HttpStatusCode.OK, (await Put(h.Client, "p1", TestHost.Project("p1", "Geforceerd"))).StatusCode);
        // Expecting version 0 means "only create": fails when it exists.
        Assert.Equal(HttpStatusCode.Conflict, (await Put(h.Client, "p1", TestHost.Project("p1"), "\"0\"")).StatusCode);
    });

    private static Task IfMatchStarOnMissing() => WithHost(async h =>
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Put(h.Client, "nieuw", TestHost.Project("nieuw"), "*")).StatusCode);
        await Put(h.Client, "nieuw", TestHost.Project("nieuw"));
        Assert.Equal(HttpStatusCode.OK, (await Put(h.Client, "nieuw", TestHost.Project("nieuw"), "*")).StatusCode);
    });

    private static Task Validation() => WithHost(async h =>
    {
        async Task<JsonElement> Errors(string id, object body)
        {
            var r = await Put(h.Client, id, body);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return (await TestHost.ReadJson(r)).GetProperty("errors");
        }

        Assert.True((await Errors("p1", new { id = "p1", name = "", created = "2026-10-09T09:00:00Z", updated = "2026-10-09T09:00:00Z", items = Array.Empty<object>() })).TryGetProperty("name", out _));
        Assert.True((await Errors("p1", new { id = "p1", name = "x", created = "2026-10-09T09:00:00Z", updated = "2026-10-09T09:00:00Z", items = new { } })).TryGetProperty("items", out _));
        Assert.True((await Errors("p1", new { id = "p1", name = "x", created = "gisteren", updated = "2026-10-09T09:00:00Z", items = Array.Empty<object>() })).TryGetProperty("created", out _));
        Assert.True((await Errors("p1", TestHost.Project("p2"))).TryGetProperty("id", out _));
        Assert.True((await Errors("a.b", TestHost.Project("a.b"))).TryGetProperty("id", out _));
        Assert.True((await Errors("p1", TestHost.Project("p1", new string('x', 201)))).TryGetProperty("name", out _));

        var notJson = await h.Client.PutAsync("/api/projects/p1", new StringContent("{kapot", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/api/projects/p1")).StatusCode);
    });

    private static Task MalformedIfMatch() => WithHost(async h =>
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(h.Client, "p1", TestHost.Project("p1"), "\"abc\"")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Delete(h.Client, "p1", "-5")).StatusCode);
    });

    private static Task SinceCursor() => WithHost(async h =>
    {
        await Put(h.Client, "a", TestHost.Project("a", "A"));
        await Put(h.Client, "b", TestHost.Project("b", "B"));
        var first = await TestHost.ReadJson(await h.Client.GetAsync("/api/projects"));
        Assert.Equal(2, first.GetProperty("projects").GetArrayLength());
        var cursor = first.GetProperty("serverTime").GetString()!;

        // Nothing changed yet.
        var none = await TestHost.ReadJson(await h.Client.GetAsync($"/api/projects?since={Uri.EscapeDataString(cursor)}"));
        Assert.Equal(0, none.GetProperty("projects").GetArrayLength());

        await Put(h.Client, "a", TestHost.Project("a", "A2"));
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(h.Client, "b")).StatusCode);

        var changes = await TestHost.ReadJson(await h.Client.GetAsync($"/api/projects?since={Uri.EscapeDataString(cursor)}"));
        var list = changes.GetProperty("projects").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal(2, list.Count);
        Assert.Equal("A2", list["a"].GetProperty("name").GetString());
        Assert.True(list["b"].GetProperty("deleted").GetBoolean());
        Assert.Equal(0, list["b"].GetProperty("items").GetArrayLength());

        // Default list hides tombstones; includeDeleted shows them.
        var active = await TestHost.ReadJson(await h.Client.GetAsync("/api/projects"));
        Assert.Equal(1, active.GetProperty("projects").GetArrayLength());
        var everything = await TestHost.ReadJson(await h.Client.GetAsync("/api/projects?includeDeleted=true"));
        Assert.Equal(2, everything.GetProperty("projects").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/api/projects/b")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(h.Client, "b")).StatusCode);

        // The new cursor moves past those changes.
        var next = changes.GetProperty("serverTime").GetString()!;
        var after = await TestHost.ReadJson(await h.Client.GetAsync($"/api/projects?since={Uri.EscapeDataString(next)}"));
        Assert.Equal(0, after.GetProperty("projects").GetArrayLength());
    });

    private static Task DeleteConflict() => WithHost(async h =>
    {
        await Put(h.Client, "p1", TestHost.Project("p1"));
        await Put(h.Client, "p1", TestHost.Project("p1", "v2"));
        Assert.Equal(HttpStatusCode.Conflict, (await Delete(h.Client, "p1", "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(h.Client, "p1", "\"2\"")).StatusCode);
    });

    private static Task RecreateAfterDelete() => WithHost(async h =>
    {
        await Put(h.Client, "p1", TestHost.Project("p1"));
        await Delete(h.Client, "p1");
        var again = await Put(h.Client, "p1", TestHost.Project("p1", "Terug"));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        // Version keeps counting, so old clients holding version 1 still get a conflict.
        Assert.Equal(3, (await TestHost.ReadJson(again)).GetProperty("version").GetInt64());
    });

    private static async Task Persistence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bouwplannen-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var h = await TestHost.StartAsync(dir))
            {
                await Put(h.Client, "p1", TestHost.Project("p1"));
                await Put(h.Client, "p1", TestHost.Project("p1", "Na herstart"));
                await Put(h.Client, "weg", TestHost.Project("weg"));
                await Delete(h.Client, "weg");
            }
            Assert.False(Directory.EnumerateFiles(Path.Combine(dir, "projects"), "*.tmp").Any());
            await using (var h = await TestHost.StartAsync(dir))
            {
                var doc = await TestHost.ReadJson(await h.Client.GetAsync("/api/projects/p1"));
                Assert.Equal("Na herstart", doc.GetProperty("name").GetString());
                Assert.Equal(2, doc.GetProperty("version").GetInt64());
                Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/api/projects/weg")).StatusCode);
            }
        }
        finally
        {
            TestHost.DeleteFolder(dir);
        }
    }

    private static async Task DamagedFileSkipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bouwplannen-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var h = await TestHost.StartAsync(dir))
                await Put(h.Client, "goed", TestHost.Project("goed"));
            await File.WriteAllTextAsync(Path.Combine(dir, "projects", "kapot.json"), "{ dit is geen json");
            await using (var h = await TestHost.StartAsync(dir))
            {
                var list = await TestHost.ReadJson(await h.Client.GetAsync("/api/projects"));
                Assert.Equal(1, list.GetProperty("projects").GetArrayLength());
            }
        }
        finally
        {
            TestHost.DeleteFolder(dir);
        }
    }

    private static Task ParallelWrites() => WithHost(async h =>
    {
        var tasks = Enumerable.Range(1, 25).Select(i => Put(h.Client, "p1", TestHost.Project("p1", $"Versie {i}")));
        var results = await Task.WhenAll(tasks);
        Assert.True(results.All(r => r.IsSuccessStatusCode));
        var doc = await TestHost.ReadJson(await h.Client.GetAsync("/api/projects/p1"));
        Assert.Equal(25, doc.GetProperty("version").GetInt64());
    });

    private static Task TooLarge() => WithHost(async h =>
    {
        var big = new string('x', (int)BouwplannenApp.MaxRequestBytes + 10);
        var body = TestHost.Project("p1", items: [new { id = "i1", kind = "maten", notes = big }]);
        var r = await Put(h.Client, "p1", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    });

    private static Task Cors() => WithHost(async h =>
    {
        async Task<HttpResponseMessage> Preflight(string origin)
        {
            var req = new HttpRequestMessage(HttpMethod.Options, "/api/projects/p1");
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", "PUT");
            req.Headers.Add("Access-Control-Request-Headers", "x-api-key,content-type,if-match");
            using var anon = h.ClientWithoutKey();
            return await anon.SendAsync(req);
        }

        var allowed = await Preflight("https://bouwplannen.example");
        Assert.True(allowed.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins) && origins.Single() == "https://bouwplannen.example");
        var denied = await Preflight("https://kwaadwillend.example");
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        req.Headers.Add("Origin", "https://bouwplannen.example");
        var r = await h.Client.SendAsync(req);
        Assert.True(r.Headers.TryGetValues("Access-Control-Expose-Headers", out var exposed) && exposed.Any(v => v.Contains("ETag")));
    });
}
