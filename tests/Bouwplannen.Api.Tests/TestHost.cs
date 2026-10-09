using System.Net.Http.Json;
using System.Text.Json;
using Bouwplannen.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Bouwplannen.Api.Tests;

/// <summary>
/// Runs the real API on a free local port, with its own data folder.
/// No mocks: requests go through Kestrel, routing, middleware and the file storage.
/// </summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string ApiKey = "test-sleutel-123";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;
    public string DataDirectory { get; }
    public HttpClient Client { get; }
    public Uri BaseAddress { get; }

    private TestHost(WebApplication app, string dataDirectory, Uri baseAddress)
    {
        _app = app;
        DataDirectory = dataDirectory;
        BaseAddress = baseAddress;
        Client = new HttpClient { BaseAddress = baseAddress };
        Client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
    }

    public static async Task<TestHost> StartAsync(string? dataDirectory = null, string? apiKey = ApiKey)
    {
        var dir = dataDirectory ?? Path.Combine(Path.GetTempPath(), "bouwplannen-tests", Guid.NewGuid().ToString("N"));
        var app = BouwplannenApp.Build([], builder =>
        {
            builder.Environment.EnvironmentName = "Testing";
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataDirectory"] = dir,
                ["Auth:ApiKey"] = apiKey,
                ["Cors:AllowedOrigins:0"] = "https://bouwplannen.example",
            });
        });
        await app.StartAsync();
        var address = new Uri(app.Urls.First());
        return new TestHost(app, dir, address);
    }

    public HttpClient ClientWithoutKey() => new() { BaseAddress = BaseAddress };

    public static object Project(string id, string name = "Schuur", object[]? items = null, string updated = "2026-10-09T10:00:00.000Z") => new
    {
        id,
        name,
        created = "2026-10-09T09:00:00.000Z",
        updated,
        items = items ?? new object[] { new { id = "i1", kind = "hoek", name = "Hoek", notes = "", a = "1", b = "1", c = "1,414" } },
    };

    public static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Json);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
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
