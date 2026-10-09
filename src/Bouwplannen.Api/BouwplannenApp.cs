using System.Reflection;
using System.Text;
using Bouwplannen.Api.Projects;
using Bouwplannen.Api.Security;

namespace Bouwplannen.Api;

/// <summary>Builds the web application. Separate from Program so tests can start it with their own settings.</summary>
public static class BouwplannenApp
{
    /// <summary>Largest accepted request body. A big project with hundreds of measurements is well under 1 MB.</summary>
    public const long MaxRequestBytes = 5 * 1024 * 1024;

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxRequestBytes);

        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IProjectRepository, FileProjectRepository>();
        builder.Services.AddProblemDetails();

        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            if (origins.Length > 0)
                p.WithOrigins(origins)
                    .AllowAnyHeader()
                    .WithMethods("GET", "PUT", "DELETE")
                    .WithExposedHeaders("ETag");
        }));

        var app = builder.Build();

        var apiKey = app.Configuration[$"{AuthOptions.Section}:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (!app.Environment.IsDevelopment())
                app.Logger.LogWarning("Geen API-sleutel ingesteld (Auth__ApiKey): iedereen die de server bereikt kan projecten lezen en wijzigen.");
            apiKey = null;
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseCors();
        app.UseMiddleware<ApiKeyMiddleware>(new ApiKeySecret(apiKey is null ? null : Encoding.UTF8.GetBytes(apiKey)));

        app.MapGet("/api/health", () => TypedResults.Ok(new
        {
            status = "ok",
            version = typeof(BouwplannenApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
        }));
        app.MapProjectEndpoints();

        // Make sure the data folder is usable before the first request, not during it.
        _ = app.Services.GetRequiredService<IProjectRepository>();
        return app;
    }
}
