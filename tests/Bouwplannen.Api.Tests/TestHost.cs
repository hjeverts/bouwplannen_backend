using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bouwplannen.Api;
using Bouwplannen.Api.Accounts;
using Bouwplannen.Api.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bouwplannen.Api.Tests;

/// <summary>
/// Runs the real API on a free local port, with its own data folder.
/// No mocks: requests go through Kestrel, routing, cookies, middleware and the file storage.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string Password = "geheim-wachtwoord-1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;
    public string DataDirectory { get; }
    public Uri BaseAddress { get; }
    /// <summary>Changes accounts directly in the data folder, like the command line tool does.</summary>
    public AccountStore Accounts { get; }

    private TestHost(WebApplication app, string dataDirectory, Uri baseAddress)
    {
        _app = app;
        DataDirectory = dataDirectory;
        BaseAddress = baseAddress;
        Accounts = new AccountStore(Options.Create(new StorageOptions { DataDirectory = dataDirectory }), TimeProvider.System);
    }

    public static string NewDataDirectory() => Path.Combine(Path.GetTempPath(), "bouwplannen-tests", Guid.NewGuid().ToString("N"));

    public static async Task<TestHost> StartAsync(string? dataDirectory = null)
    {
        var dir = dataDirectory ?? NewDataDirectory();
        var app = BouwplannenApp.Build([], builder =>
        {
            builder.Environment.EnvironmentName = "Testing";
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataDirectory"] = dir });
        });
        await app.StartAsync();
        return new TestHost(app, dir, new Uri(app.Urls.First()));
    }

    /// <summary>A browser-like client: keeps cookies and sends the header the app sends.</summary>
    public HttpClient NewClient(bool csrfHeader = true)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) { BaseAddress = BaseAddress };
        if (csrfHeader) client.DefaultRequestHeaders.Add("X-Requested-With", "bouwplannen");
        return client;
    }

    public UserRecord CreateUser(string username, bool admin = false) => Accounts.CreateUser(username, null, Password, admin);

    /// <summary>Creates the user if needed and returns a logged-in client.</summary>
    public async Task<User> LoginAsync(string username, bool admin = false)
    {
        var record = Accounts.FindByUsername(username) ?? CreateUser(username, admin);
        var client = NewClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password = Password });
        if (res.StatusCode != HttpStatusCode.OK) throw new AssertionException($"Inloggen als {username} mislukt: {(int)res.StatusCode}");
        var me = await ReadJson(res);
        return new User(record.Id, username, me.GetProperty("personalGroupId").GetString()!, client);
    }

    public static object Project(string id, string groupId, string name = "Schuur", object[]? items = null, string updated = "2026-10-09T10:00:00.000Z") => new
    {
        id,
        name,
        created = "2026-10-09T09:00:00.000Z",
        updated,
        groupId,
        items = items ?? new object[] { new { id = "i1", kind = "hoek", name = "Hoek", notes = "", a = "1", b = "1", c = "1,414" } },
    };

    public static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public static void DeleteFolder(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

public sealed record User(string Id, string Username, string PersonalGroupId, HttpClient Http);
