using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bouwplannen.Api.Accounts;
using static Bouwplannen.Api.Tests.ProjectApiTests;

namespace Bouwplannen.Api.Tests;

/// <summary>Login, sessions, groups and the command line tool.</summary>
public static class AccountTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> All() =>
    [
        ("login: right password works, wrong or unknown gets the same answer", Login),
        ("login: locked after 5 failures, Retry-After", LoginThrottle),
        ("login throttle: doubles the wait, resets after success", ThrottleUnit),
        ("login: max 10 attempts per minute per address", LoginRateLimit),
        ("disabled user cannot log in and is logged out", DisabledUser),
        ("changing requests without the app header are refused (CSRF)", Csrf),
        ("cookie is HttpOnly and SameSite=Strict", CookieFlags),
        ("me: user, admin flag, private group", Me),
        ("logout and log out everywhere", Logout),
        ("password change: old sessions end, new password works", PasswordChange),
        ("groups: create, add member, member sees shared projects", GroupSharing),
        ("groups: only owners manage members, members can leave", GroupPermissions),
        ("groups: removed member loses access and is told which groups remain", RemovedMember),
        ("groups: moving a project tells members of the old group", MoveProject),
        ("groups: private group cannot be shared or deleted", PrivateGroup),
        ("groups: delete only when empty", DeleteGroup),
        ("passwords are hashed with PBKDF2 and a salt", Hashing),
        ("command line: add, list, passwd, admin, disable, logout", Cli),
        ("command line: account works at once in the running API", CliWhileRunning),
    ];

    private static async Task<HttpResponseMessage> Login(HttpClient c, string username, string password) =>
        await c.PostAsJsonAsync("/api/auth/login", new { username, password });

    private static Task Login() => WithHost(async h =>
    {
        h.CreateUser("hans");
        using var c = h.NewClient();
        var ok = await Login(c, "Hans", TestHost.Password); // usernames are case-insensitive
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/projects")).StatusCode);

        using var other = h.NewClient();
        var wrong = await TestHost.ReadJson(await Login(other, "hans", "fout-wachtwoord"));
        var unknown = await TestHost.ReadJson(await Login(other, "niemand", "fout-wachtwoord"));
        Assert.Equal(wrong.GetProperty("title").GetString(), unknown.GetProperty("title").GetString());
        Assert.Equal(401, unknown.GetProperty("status").GetInt32());
    });

    private static Task LoginThrottle() => WithHost(async h =>
    {
        h.CreateUser("hans");
        using var c = h.NewClient();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "hans", "fout")).StatusCode);
        var locked = await Login(c, "hans", TestHost.Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter is not null);
        Assert.True((await TestHost.ReadJson(locked)).GetProperty("title").GetString()!.Contains("Te veel"));
    });

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static Task ThrottleUnit()
    {
        var time = new FakeTime();
        var t = new Accounts.LoginThrottle(time);
        for (var i = 0; i < 4; i++) t.Failed("hans");
        Assert.True(t.Wait("hans") is null);
        t.Failed("hans");
        Assert.Equal(TimeSpan.FromSeconds(30), t.Wait("hans"));
        time.Now += TimeSpan.FromSeconds(31);
        Assert.True(t.Wait("hans") is null);
        t.Failed("hans");
        Assert.Equal(TimeSpan.FromSeconds(60), t.Wait("hans"));
        for (var i = 0; i < 20; i++) t.Failed("hans");
        Assert.Equal(TimeSpan.FromMinutes(15), t.Wait("hans"));
        t.Succeeded("hans");
        Assert.True(t.Wait("hans") is null);
        Assert.True(t.Wait("piet") is null);
        return Task.CompletedTask;
    }

    private static Task LoginRateLimit() => WithHost(async h =>
    {
        using var c = h.NewClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++) codes.Add((await Login(c, $"iemand{i}", "fout")).StatusCode);
        Assert.Equal(10, codes.Count(x => x == HttpStatusCode.Unauthorized));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
    });

    private static Task DisabledUser() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        h.Accounts.ModifyUser(hans.Id, u => u.Disabled = true, newStamp: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hans.Http.GetAsync("/api/projects")).StatusCode);
        using var c = h.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "hans", TestHost.Password)).StatusCode);
    });

    private static Task Csrf() => WithHost(async h =>
    {
        h.CreateUser("hans");
        using var bare = h.NewClient(csrfHeader: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await Login(bare, "hans", TestHost.Password)).StatusCode);
        var hans = await h.LoginAsync("hans");
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/projects/p1") { Content = JsonContent.Create(TestHost.Project("p1", hans.PersonalGroupId)) };
        req.Headers.Remove("X-Requested-With");
        hans.Http.DefaultRequestHeaders.Remove("X-Requested-With");
        Assert.Equal(HttpStatusCode.Forbidden, (await hans.Http.SendAsync(req)).StatusCode);
        // Reading is fine without the header.
        Assert.Equal(HttpStatusCode.OK, (await hans.Http.GetAsync("/api/projects")).StatusCode);
    });

    private static Task CookieFlags() => WithHost(async h =>
    {
        h.CreateUser("hans");
        using var c = h.NewClient();
        var res = await Login(c, "hans", TestHost.Password);
        var cookie = res.Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(BouwplannenApp.CookieName));
        Assert.True(cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        Assert.True(cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        Assert.True(cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));

        // Behind the HTTPS proxy the cookie is marked Secure.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { username = "hans", password = TestHost.Password }) };
        req.Headers.Add("X-Forwarded-Proto", "https");
        using var proxied = h.NewClient();
        var secure = (await proxied.SendAsync(req)).Headers.GetValues("Set-Cookie").Single(v => v.StartsWith(BouwplannenApp.CookieName));
        Assert.True(secure.Contains("secure", StringComparison.OrdinalIgnoreCase));
    });

    private static Task Me() => WithHost(async h =>
    {
        var piet = await h.LoginAsync("piet");
        var jan = await h.LoginAsync("jan");
        var me = await TestHost.ReadJson(await jan.Http.GetAsync("/api/auth/me"));
        Assert.Equal("jan", me.GetProperty("username").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        var groups = me.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(1, groups.Count);
        Assert.True(groups[0].GetProperty("personal").GetBoolean());
        Assert.Equal(jan.PersonalGroupId, groups[0].GetProperty("id").GetString());
        Assert.True((await TestHost.ReadJson(await piet.Http.GetAsync("/api/auth/me"))).GetProperty("isAdmin").GetBoolean());
    });

    private static Task Logout() => WithHost(async h =>
    {
        var tablet = await h.LoginAsync("hans");
        var phone = await h.LoginAsync("hans");
        Assert.Equal(HttpStatusCode.NoContent, (await tablet.Http.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.Http.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.Http.GetAsync("/api/auth/me")).StatusCode);

        var laptop = await h.LoginAsync("hans");
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.Http.PostAsync("/api/auth/logout-all", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.Http.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.Http.GetAsync("/api/auth/me")).StatusCode);
    });

    private static Task PasswordChange() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        var other = await h.LoginAsync("hans");
        var wrong = await hans.Http.PostAsJsonAsync("/api/auth/password", new { current = "fout", @new = "nieuw-wachtwoord-2" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var weak = await hans.Http.PostAsJsonAsync("/api/auth/password", new { current = TestHost.Password, @new = "kort" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await hans.Http.PostAsJsonAsync("/api/auth/password", new { current = TestHost.Password, @new = "nieuw-wachtwoord-2" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.Http.GetAsync("/api/auth/me")).StatusCode);
        using var c = h.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Login(c, "hans", TestHost.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login(c, "hans", "nieuw-wachtwoord-2")).StatusCode);
    });

    private static async Task<JsonElement> CreateGroup(User u, string name)
    {
        var res = await u.Http.PostAsJsonAsync("/api/groups", new { name });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await TestHost.ReadJson(res);
    }

    private static Task GroupSharing() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        var piet = await h.LoginAsync("piet");
        var team = await CreateGroup(hans, "Verbouwing Dorpsstraat");
        var gid = team.GetProperty("id").GetString()!;
        Assert.True(team.GetProperty("isOwner").GetBoolean());

        await Put(hans.Http, "gedeeld", TestHost.Project("gedeeld", gid, "Kap"));
        Assert.Equal(HttpStatusCode.NotFound, (await piet.Http.GetAsync("/api/projects/gedeeld")).StatusCode);

        var added = await hans.Http.PostAsJsonAsync($"/api/groups/{gid}/members", new { username = "piet" });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(2, (await TestHost.ReadJson(added)).GetProperty("members").GetArrayLength());

        // Piet sees it now, and can change it.
        var doc = await TestHost.ReadJson(await piet.Http.GetAsync("/api/projects/gedeeld"));
        Assert.Equal("Kap", doc.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Put(piet.Http, "gedeeld", TestHost.Project("gedeeld", gid, "Kap door Piet"))).StatusCode);
        Assert.Equal("Kap door Piet", (await TestHost.ReadJson(await hans.Http.GetAsync("/api/projects/gedeeld"))).GetProperty("name").GetString());

        var groups = await TestHost.ReadJson(await piet.Http.GetAsync("/api/groups"));
        var shared = groups.EnumerateArray().Single(g => g.GetProperty("id").GetString() == gid);
        Assert.False(shared.GetProperty("isOwner").GetBoolean());
        Assert.Equal(1, shared.GetProperty("projectCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await hans.Http.PostAsJsonAsync($"/api/groups/{gid}/members", new { username = "niemand" })).StatusCode);
    });

    private static Task GroupPermissions() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        var piet = await h.LoginAsync("piet");
        var jan = await h.LoginAsync("jan");
        var gid = (await CreateGroup(hans, "Team")).GetProperty("id").GetString()!;
        await hans.Http.PostAsJsonAsync($"/api/groups/{gid}/members", new { username = "piet" });

        // A member cannot add others, rename or remove others.
        Assert.Equal(HttpStatusCode.Forbidden, (await piet.Http.PostAsJsonAsync($"/api/groups/{gid}/members", new { username = "jan" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await piet.Http.PutAsJsonAsync($"/api/groups/{gid}", new { name = "Overgenomen" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await piet.Http.DeleteAsync($"/api/groups/{gid}/members/{hans.Id}")).StatusCode);
        // Someone outside the group cannot see it at all.
        Assert.Equal(HttpStatusCode.NotFound, (await jan.Http.PutAsJsonAsync($"/api/groups/{gid}", new { name = "X" })).StatusCode);
        // The only owner cannot leave while others remain.
        Assert.Equal(HttpStatusCode.BadRequest, (await hans.Http.DeleteAsync($"/api/groups/{gid}/members/{hans.Id}")).StatusCode);
        // Make Piet owner, then Piet may add Jan.
        Assert.Equal(HttpStatusCode.OK, (await hans.Http.PostAsJsonAsync($"/api/groups/{gid}/members", new { username = "piet", owner = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await piet.Http.PostAsJsonAsync($"/api/groups/{gid}/members", new { username = "jan" })).StatusCode);
        // Jan leaves by himself.
        Assert.Equal(HttpStatusCode.NoContent, (await jan.Http.DeleteAsync($"/api/groups/{gid}/members/{jan.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hans.Http.PutAsJsonAsync($"/api/groups/{gid}", new { name = "Team Noord" })).StatusCode);
    });

    private static Task RemovedMember() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        var piet = await h.LoginAsync("piet");
        var gid = (await CreateGroup(hans, "Team")).GetProperty("id").GetString()!;
        await hans.Http.PostAsJsonAsync($"/api/groups/{gid}/members", new { username = "piet" });
        await Put(hans.Http, "p1", TestHost.Project("p1", gid));
        var before = await TestHost.ReadJson(await piet.Http.GetAsync("/api/projects"));
        Assert.True(before.GetProperty("groupIds").EnumerateArray().Any(g => g.GetString() == gid));

        Assert.Equal(HttpStatusCode.NoContent, (await hans.Http.DeleteAsync($"/api/groups/{gid}/members/{piet.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await piet.Http.GetAsync("/api/projects/p1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(piet.Http, "p1", TestHost.Project("p1", gid))).StatusCode);
        var after = await TestHost.ReadJson(await piet.Http.GetAsync("/api/projects"));
        Assert.False(after.GetProperty("groupIds").EnumerateArray().Any(g => g.GetString() == gid));
    });

    private static Task MoveProject() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        var piet = await h.LoginAsync("piet");
        var team = (await CreateGroup(hans, "Team")).GetProperty("id").GetString()!;
        await hans.Http.PostAsJsonAsync($"/api/groups/{team}/members", new { username = "piet" });
        await Put(hans.Http, "p1", TestHost.Project("p1", team));
        var cursor = (await TestHost.ReadJson(await piet.Http.GetAsync("/api/projects"))).GetProperty("serverTime").GetString()!;

        // Piet cannot move it into Hans's private group; Hans can.
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(piet.Http, "p1", TestHost.Project("p1", hans.PersonalGroupId))).StatusCode);
        var moved = await Put(hans.Http, "p1", TestHost.Project("p1", hans.PersonalGroupId));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        var changes = await TestHost.ReadJson(await piet.Http.GetAsync($"/api/projects?since={Uri.EscapeDataString(cursor)}"));
        Assert.Equal(0, changes.GetProperty("projects").GetArrayLength());
        Assert.Equal("p1", changes.GetProperty("removed")[0].GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await piet.Http.GetAsync("/api/projects/p1")).StatusCode);
    });

    private static Task PrivateGroup() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        await h.LoginAsync("piet");
        Assert.Equal(HttpStatusCode.BadRequest, (await hans.Http.PostAsJsonAsync($"/api/groups/{hans.PersonalGroupId}/members", new { username = "piet" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await hans.Http.DeleteAsync($"/api/groups/{hans.PersonalGroupId}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await hans.Http.PutAsJsonAsync($"/api/groups/{hans.PersonalGroupId}", new { name = "Gedeeld" })).StatusCode);
    });

    private static Task DeleteGroup() => WithHost(async h =>
    {
        var hans = await h.LoginAsync("hans");
        var gid = (await CreateGroup(hans, "Tijdelijk")).GetProperty("id").GetString()!;
        await Put(hans.Http, "p1", TestHost.Project("p1", gid));
        var refused = await hans.Http.DeleteAsync($"/api/groups/{gid}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.True((await TestHost.ReadJson(refused)).GetProperty("title").GetString()!.Contains("1 project"));
        await Delete(hans.Http, "p1");
        Assert.Equal(HttpStatusCode.NoContent, (await hans.Http.DeleteAsync($"/api/groups/{gid}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await hans.Http.PostAsJsonAsync("/api/groups", new { name = "" })).StatusCode);
    });

    private static Task Hashing()
    {
        var a = PasswordHasher.Hash("hetzelfde-wachtwoord");
        var b = PasswordHasher.Hash("hetzelfde-wachtwoord");
        Assert.True(a.StartsWith($"pbkdf2-sha256${PasswordHasher.Iterations}$"));
        Assert.False(a == b); // different salt
        Assert.True(PasswordHasher.Verify("hetzelfde-wachtwoord", a));
        Assert.False(PasswordHasher.Verify("ander-wachtwoord", a));
        Assert.False(PasswordHasher.Verify("x", "kapotte-hash"));
        Assert.True(PasswordHasher.Check("kort") is not null);
        return Task.CompletedTask;
    }

    private static (int Code, string Out, string Err) RunCli(string dir, string[] args, params string[] secrets)
    {
        var queue = new Queue<string>(secrets);
        var output = new StringWriter();
        var error = new StringWriter();
        Environment.SetEnvironmentVariable("Storage__DataDirectory", dir);
        try
        {
            var code = UserCli.Run(args, new StringReader(""), output, error, () => queue.Count > 0 ? queue.Dequeue() : null);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Storage__DataDirectory", null);
        }
    }

    private static Task Cli()
    {
        var dir = TestHost.NewDataDirectory();
        try
        {
            var pw = "lang-genoeg-wachtwoord";
            var first = RunCli(dir, ["add", "Hans", "--name", "Hans Everts"], pw, pw);
            Assert.Equal(0, first.Code);
            Assert.True(first.Out.Contains("(beheerder)")); // first account
            Assert.Equal(0, RunCli(dir, ["add", "piet"], pw, pw).Code);
            Assert.Equal(1, RunCli(dir, ["add", "piet"], pw, pw).Code);
            var mismatch = RunCli(dir, ["add", "jan"], pw, "anders-wachtwoord");
            Assert.Equal(1, mismatch.Code);
            Assert.True(mismatch.Err.Contains("niet gelijk"));
            Assert.True(RunCli(dir, ["add", "jan"], "kort").Err.Contains("minstens"));
            Assert.True(RunCli(dir, ["add", "Jan Jansen"], pw, pw).Err.Contains("gebruikersnaam"));

            var list = RunCli(dir, ["list"]);
            Assert.True(list.Out.Contains("hans") && list.Out.Contains("Hans Everts") && list.Out.Contains("beheerder") && list.Out.Contains("piet"));

            Assert.Equal(0, RunCli(dir, ["passwd", "piet"], "nieuw-lang-wachtwoord", "nieuw-lang-wachtwoord").Code);
            Assert.Equal(0, RunCli(dir, ["admin", "piet", "on"]).Code);
            Assert.Equal(0, RunCli(dir, ["admin", "hans", "off"]).Code);
            Assert.Equal(1, RunCli(dir, ["admin", "piet", "off"]).Code); // last administrator
            Assert.Equal(0, RunCli(dir, ["disable", "hans"]).Code);
            Assert.True(RunCli(dir, ["list"]).Out.Contains("geblokkeerd"));
            Assert.Equal(0, RunCli(dir, ["enable", "hans"]).Code);
            Assert.Equal(0, RunCli(dir, ["logout", "hans"]).Code);
            Assert.Equal(1, RunCli(dir, ["passwd", "niemand"], pw, pw).Code);
            Assert.Equal(2, RunCli(dir, ["onzin"]).Code);
            Assert.Equal(2, RunCli(dir, []).Code);
        }
        finally
        {
            TestHost.DeleteFolder(dir);
        }
        return Task.CompletedTask;
    }

    private static Task CliWhileRunning() => WithHost(async h =>
    {
        var pw = "lang-genoeg-wachtwoord";
        Assert.Equal(0, RunCli(h.DataDirectory, ["add", "nieuw"], pw, pw).Code);
        using var c = h.NewClient();
        Assert.Equal(HttpStatusCode.OK, (await Login(c, "nieuw", pw)).StatusCode);

        // Disabled on the command line: the running API logs the user out at the next request.
        Assert.Equal(0, RunCli(h.DataDirectory, ["disable", "nieuw"]).Code);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
    });
}
