using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bouwplannen.Api.Accounts;

namespace Bouwplannen.Api.Tests;

/// <summary>Projects: storage, versions, sync cursor and access per group.</summary>
public static class ProjectApiTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All() =>
    [
        ("health is open, projects need a login", NeedsLogin),
        ("create, read back, items stored unchanged", CreateAndRead),
        ("project settings (unit) are stored as sent and validated", SettingsStored),
        ("update with If-Match, conflict on a stale version", OptimisticConcurrency),
        ("If-Match: * on a missing project is 404", IfMatchStarOnMissing),
        ("invalid input gives validation errors (group required)", Validation),
        ("malformed If-Match is a 400", MalformedIfMatch),
        ("since cursor returns only changes, deletions as tombstones", SinceCursor),
        ("delete with a stale version is a conflict", DeleteConflict),
        ("a deleted project can be stored again", RecreateAfterDelete),
        ("data survives a restart", Persistence),
        ("a damaged file is skipped, the rest still loads", DamagedFileSkipped),
        ("parallel writes lose no versions", ParallelWrites),
        ("oversized body is rejected", TooLarge),
        ("private projects are invisible to others and cannot be overwritten", PrivateProjects),
        ("projects from before accounts go to the first administrator", OrphansToAdmin),
    ];

    internal static async Task WithHost(Func<TestHost, Task> test)
    {
        var host = await TestHost.StartAsync();
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

    internal static async Task<HttpResponseMessage> Put(HttpClient c, string id, object body, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{id}") { Content = JsonContent.Create(body) };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    internal static async Task<HttpResponseMessage> Delete(HttpClient c, string id, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{id}");
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await c.SendAsync(req);
    }

    private static Task NeedsLogin() => WithHost(async h =>
    {
        using var anon = h.NewClient();
        var r = await anon.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(anon, "p1", TestHost.Project("p1", "g"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/groups")).StatusCode);
    });

    private static Task CreateAndRead() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        var put = await Put(hans.Http, "p1", TestHost.Project("p1", hans.PersonalGroupId));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        Assert.Equal("\"1\"", put.Headers.ETag?.Tag);
        var created = await TestHost.ReadJson(put);
        Assert.Equal(1, created.GetProperty("version").GetInt64());
        Assert.Equal(hans.PersonalGroupId, created.GetProperty("groupId").GetString());

        var doc = await TestHost.ReadJson(await hans.Http.GetAsync("/api/projects/p1"));
        Assert.Equal("Schuur", doc.GetProperty("name").GetString());
        Assert.Equal("2026-10-09T10:00:00.000Z", doc.GetProperty("updated").GetString());
        Assert.Equal("1,414", doc.GetProperty("items")[0].GetProperty("c").GetString());

        var list = await TestHost.ReadJson(await hans.Http.GetAsync("/api/projects"));
        Assert.Equal(1, list.GetProperty("projects").GetArrayLength());
        Assert.True(list.GetProperty("groupIds").EnumerateArray().Any(g => g.GetString() == hans.PersonalGroupId));
        Assert.Equal(HttpStatusCode.NotFound, (await hans.Http.GetAsync("/api/projects/onbekend")).StatusCode);
    });

    private static Task SettingsStored() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        var g = hans.PersonalGroupId;
        var body = new { id = "p1", name = "Schuur", created = "2026-10-09T10:00:00.000Z", updated = "2026-10-09T10:00:00.000Z", groupId = g, items = Array.Empty<object>(), settings = new { unit = "mm" } };
        Assert.Equal(HttpStatusCode.Created, (await Put(hans.Http, "p1", body)).StatusCode);
        var doc = await TestHost.ReadJson(await hans.Http.GetAsync("/api/projects/p1"));
        Assert.Equal("mm", doc.GetProperty("settings").GetProperty("unit").GetString());
        // Without settings (older apps) it is simply absent.
        Assert.Equal(HttpStatusCode.OK, (await Put(hans.Http, "p1", TestHost.Project("p1", g), "\"1\"")).StatusCode);
        var again = await TestHost.ReadJson(await hans.Http.GetAsync("/api/projects/p1"));
        Assert.True(!again.TryGetProperty("settings", out var s) || s.ValueKind == JsonValueKind.Null);
        var bad = new { id = "p2", name = "x", created = "2026-10-09T10:00:00.000Z", updated = "2026-10-09T10:00:00.000Z", groupId = g, items = Array.Empty<object>(), settings = "mm" };
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(hans.Http, "p2", bad)).StatusCode);
    });

    private static Task OptimisticConcurrency() => WithHost(async h =>
    {
        var u = await h.LoginAsync("hans");
        var g = u.PersonalGroupId;
        await Put(u.Http, "p1", TestHost.Project("p1", g));
        var ok = await Put(u.Http, "p1", TestHost.Project("p1", g, "Schuur v2"), "\"1\"");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(2, (await TestHost.ReadJson(ok)).GetProperty("version").GetInt64());

        var stale = await Put(u.Http, "p1", TestHost.Project("p1", g, "Van ander apparaat"), "\"1\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await TestHost.ReadJson(stale);
        Assert.Equal("Schuur v2", current.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Put(u.Http, "p1", TestHost.Project("p1", g, "Geforceerd"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(u.Http, "p1", TestHost.Project("p1", g), "\"0\"")).StatusCode);
    });

    private static Task IfMatchStarOnMissing() => WithHost(async h =>
    {
        var u = await h.LoginAsync("hans");
        Assert.Equal(HttpStatusCode.NotFound, (await Put(u.Http, "nieuw", TestHost.Project("nieuw", u.PersonalGroupId), "*")).StatusCode);
        await Put(u.Http, "nieuw", TestHost.Project("nieuw", u.PersonalGroupId));
        Assert.Equal(HttpStatusCode.OK, (await Put(u.Http, "nieuw", TestHost.Project("nieuw", u.PersonalGroupId), "*")).StatusCode);
    });

    private static Task Validation() => WithHost(async h =>
    {
        var u = await h.LoginAsync("hans");
        var g = u.PersonalGroupId;
        async Task<JsonElement> Errors(string id, object body)
        {
            var r = await Put(u.Http, id, body);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            return (await TestHost.ReadJson(r)).GetProperty("errors");
        }

        Assert.True((await Errors("p1", new { id = "p1", name = "", created = "2026-10-09T09:00:00Z", updated = "2026-10-09T09:00:00Z", groupId = g, items = Array.Empty<object>() })).TryGetProperty("name", out _));
        Assert.True((await Errors("p1", new { id = "p1", name = "x", created = "2026-10-09T09:00:00Z", updated = "2026-10-09T09:00:00Z", groupId = g, items = new { } })).TryGetProperty("items", out _));
        Assert.True((await Errors("p1", new { id = "p1", name = "x", created = "gisteren", updated = "2026-10-09T09:00:00Z", groupId = g, items = Array.Empty<object>() })).TryGetProperty("created", out _));
        Assert.True((await Errors("p1", new { id = "p1", name = "x", created = "2026-10-09T09:00:00Z", updated = "2026-10-09T09:00:00Z", items = Array.Empty<object>() })).TryGetProperty("groupId", out _));
        Assert.True((await Errors("p1", TestHost.Project("p2", g))).TryGetProperty("id", out _));
        Assert.True((await Errors("a.b", TestHost.Project("a.b", g))).TryGetProperty("id", out _));
        Assert.True((await Errors("p1", TestHost.Project("p1", g, new string('x', 201)))).TryGetProperty("name", out _));

        var notJson = await u.Http.PutAsync("/api/projects/p1", new StringContent("{kapot", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(u.Http, "p1", TestHost.Project("p1", "g-bestaatniet"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await u.Http.GetAsync("/api/projects/p1")).StatusCode);
    });

    private static Task MalformedIfMatch() => WithHost(async h =>
    {
        var u = await h.LoginAsync("hans");
        await Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId));
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId), "\"abc\"")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Delete(u.Http, "p1", "-5")).StatusCode);
    });

    private static Task SinceCursor() => WithHost(async h =>
    {
        var u = await h.LoginAsync("hans");
        var g = u.PersonalGroupId;
        await Put(u.Http, "a", TestHost.Project("a", g, "A"));
        await Put(u.Http, "b", TestHost.Project("b", g, "B"));
        var first = await TestHost.ReadJson(await u.Http.GetAsync("/api/projects"));
        Assert.Equal(2, first.GetProperty("projects").GetArrayLength());
        var cursor = first.GetProperty("serverTime").GetString()!;

        var none = await TestHost.ReadJson(await u.Http.GetAsync($"/api/projects?since={Uri.EscapeDataString(cursor)}"));
        Assert.Equal(0, none.GetProperty("projects").GetArrayLength());

        await Put(u.Http, "a", TestHost.Project("a", g, "A2"));
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(u.Http, "b")).StatusCode);

        var changes = await TestHost.ReadJson(await u.Http.GetAsync($"/api/projects?since={Uri.EscapeDataString(cursor)}"));
        var list = changes.GetProperty("projects").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal(2, list.Count);
        Assert.Equal("A2", list["a"].GetProperty("name").GetString());
        Assert.True(list["b"].GetProperty("deleted").GetBoolean());

        var active = await TestHost.ReadJson(await u.Http.GetAsync("/api/projects"));
        Assert.Equal(1, active.GetProperty("projects").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await u.Http.GetAsync("/api/projects/b")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(u.Http, "b")).StatusCode);

        var next = changes.GetProperty("serverTime").GetString()!;
        var after = await TestHost.ReadJson(await u.Http.GetAsync($"/api/projects?since={Uri.EscapeDataString(next)}"));
        Assert.Equal(0, after.GetProperty("projects").GetArrayLength());
    });

    private static Task DeleteConflict() => WithHost(async h =>
    {
        var u = await h.LoginAsync("hans");
        await Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId));
        await Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId, "v2"));
        Assert.Equal(HttpStatusCode.Conflict, (await Delete(u.Http, "p1", "\"1\"")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(u.Http, "p1", "\"2\"")).StatusCode);
    });

    private static Task RecreateAfterDelete() => WithHost(async h =>
    {
        var u = await h.LoginAsync("hans");
        await Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId));
        await Delete(u.Http, "p1");
        var again = await Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId, "Terug"));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(3, (await TestHost.ReadJson(again)).GetProperty("version").GetInt64());
    });

    private static async Task Persistence()
    {
        var dir = TestHost.NewDataDirectory();
        try
        {
            await using (var h = await TestHost.StartAsync(dir))
            {
                var u = await h.LoginAsync("hans");
                await Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId));
                await Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId, "Na herstart"));
                await Put(u.Http, "weg", TestHost.Project("weg", u.PersonalGroupId));
                await Delete(u.Http, "weg");
            }
            Assert.False(Directory.EnumerateFiles(Path.Combine(dir, "projects"), "*.tmp").Any());
            await using (var h = await TestHost.StartAsync(dir))
            {
                var u = await h.LoginAsync("hans");
                var doc = await TestHost.ReadJson(await u.Http.GetAsync("/api/projects/p1"));
                Assert.Equal("Na herstart", doc.GetProperty("name").GetString());
                Assert.Equal(2, doc.GetProperty("version").GetInt64());
                Assert.Equal(HttpStatusCode.NotFound, (await u.Http.GetAsync("/api/projects/weg")).StatusCode);
            }
        }
        finally
        {
            TestHost.DeleteFolder(dir);
        }
    }

    private static async Task DamagedFileSkipped()
    {
        var dir = TestHost.NewDataDirectory();
        try
        {
            await using (var h = await TestHost.StartAsync(dir))
            {
                var u = await h.LoginAsync("hans");
                await Put(u.Http, "goed", TestHost.Project("goed", u.PersonalGroupId));
            }
            await File.WriteAllTextAsync(Path.Combine(dir, "projects", "kapot.json"), "{ dit is geen json");
            await using (var h = await TestHost.StartAsync(dir))
            {
                var u = await h.LoginAsync("hans");
                var list = await TestHost.ReadJson(await u.Http.GetAsync("/api/projects"));
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
        var u = await h.LoginAsync("hans");
        var results = await Task.WhenAll(Enumerable.Range(1, 25).Select(i => Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId, $"Versie {i}"))));
        Assert.True(results.All(r => r.IsSuccessStatusCode));
        var doc = await TestHost.ReadJson(await u.Http.GetAsync("/api/projects/p1"));
        Assert.Equal(25, doc.GetProperty("version").GetInt64());
    });

    private static Task TooLarge() => WithHost(async h =>
    {
        var u = await h.LoginAsync("hans");
        var big = new string('x', (int)BouwplannenApp.MaxRequestBytes + 10);
        var r = await Put(u.Http, "p1", TestHost.Project("p1", u.PersonalGroupId, items: [new { id = "i1", kind = "maten", notes = big }]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    });

    private static Task PrivateProjects() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        var piet = await h.LoginAsync("piet");
        await Put(hans.Http, "geheim", TestHost.Project("geheim", hans.PersonalGroupId, "Van Hans"));

        Assert.Equal(HttpStatusCode.NotFound, (await piet.Http.GetAsync("/api/projects/geheim")).StatusCode);
        var list = await TestHost.ReadJson(await piet.Http.GetAsync("/api/projects"));
        Assert.Equal(0, list.GetProperty("projects").GetArrayLength());
        // Same id in Piet's own group: refused, Hans's project stays untouched.
        Assert.Equal(HttpStatusCode.NotFound, (await Put(piet.Http, "geheim", TestHost.Project("geheim", piet.PersonalGroupId, "Overschreven"))).StatusCode);
        // Putting it in Hans's group is not allowed either.
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(piet.Http, "x", TestHost.Project("x", hans.PersonalGroupId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(piet.Http, "geheim")).StatusCode);
        var doc = await TestHost.ReadJson(await hans.Http.GetAsync("/api/projects/geheim"));
        Assert.Equal("Van Hans", doc.GetProperty("name").GetString());
    });

    private static async Task OrphansToAdmin()
    {
        var dir = TestHost.NewDataDirectory();
        try
        {
            // A project file from the time before accounts: no groupId.
            Directory.CreateDirectory(Path.Combine(dir, "projects"));
            await File.WriteAllTextAsync(Path.Combine(dir, "projects", "oud.json"),
                """{"id":"oud","name":"Van vroeger","created":"2026-10-09T09:00:00Z","updated":"2026-10-09T09:00:00Z","items":[],"version":3,"serverModified":"2026-10-09T09:00:00+00:00","deleted":false}""");
            await using var h = await TestHost.StartAsync(dir);
            var piet = await h.LoginAsync("piet"); // first user: administrator
            var jan = await h.LoginAsync("jan");
            var doc = await TestHost.ReadJson(await piet.Http.GetAsync("/api/projects/oud"));
            Assert.Equal(piet.PersonalGroupId, doc.GetProperty("groupId").GetString());
            Assert.Equal(4, doc.GetProperty("version").GetInt64());
            Assert.Equal(HttpStatusCode.NotFound, (await jan.Http.GetAsync("/api/projects/oud")).StatusCode);
        }
        finally
        {
            TestHost.DeleteFolder(dir);
        }
    }
}
