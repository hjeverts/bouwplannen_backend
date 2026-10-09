using System.Reflection;
using System.Threading.RateLimiting;
using Bouwplannen.Api.Accounts;
using Bouwplannen.Api.Projects;
using Bouwplannen.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;

namespace Bouwplannen.Api;

/// <summary>Builds the web application. Separate from Program so tests can start it with their own settings.</summary>
public static class BouwplannenApp
{
    /// <summary>Largest accepted request body. A big project with hundreds of measurements is well under 1 MB.</summary>
    public const long MaxRequestBytes = 5 * 1024 * 1024;

    public const string LoginRateLimit = "login";
    public const string CookieName = "bouwplannen_login";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxRequestBytes);

        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IProjectRepository, FileProjectRepository>();
        builder.Services.AddSingleton<AccountStore>();
        builder.Services.AddSingleton<LoginThrottle>();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<AccountExceptionHandler>();

        // Behind nginx: take the original scheme (https) and client address from the proxy headers.
        // The API is only reachable through the proxy in the Docker network, so the headers can be trusted.
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            o.ForwardLimit = 2;
        });

        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                // Secure over HTTPS (as seen through the proxy); plain HTTP only works for local testing.
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromDays(90);
                o.SlidingExpiration = true;
                // An API answers with status codes, it does not redirect to a login page.
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                // Every request: the user must still exist, be enabled and have the same security stamp
                // (changes on password change and "log out everywhere").
                o.Events.OnValidatePrincipal = async ctx =>
                {
                    var accounts = ctx.HttpContext.RequestServices.GetRequiredService<AccountStore>();
                    var id = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    var stamp = ctx.Principal?.FindFirst(ClaimsExtensions.StampClaim)?.Value;
                    var user = id is null ? null : accounts.FindUser(id);
                    if (user is null || user.Disabled || user.SecurityStamp != stamp)
                    {
                        ctx.RejectPrincipal();
                        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                };
            });
        builder.Services.AddAuthorization();

        // Per address: at most 10 login attempts a minute (on top of the per-username lock-out).
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(LoginRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "onbekend",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        var app = builder.Build();

        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<CsrfMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapGet("/api/health", () => TypedResults.Ok(new
        {
            status = "ok",
            version = typeof(BouwplannenApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev",
        }));
        app.MapAuthEndpoints();
        app.MapGroupEndpoints();
        app.MapProjectEndpoints();

        // Make sure the data folder is usable before the first request, not during it.
        var accounts = app.Services.GetRequiredService<AccountStore>();
        var projects = app.Services.GetRequiredService<IProjectRepository>();
        if (!accounts.HasUsers)
            app.Logger.LogWarning("Er zijn nog geen gebruikers. Maak er een aan met: ./user.sh add <naam>");
        else if (accounts.Snapshot().Users.Where(u => u.IsAdmin && !u.Disabled).OrderBy(u => u.Created).FirstOrDefault() is { } admin)
        {
            var moved = projects.AssignOrphansAsync(accounts.PersonalGroup(admin.Id).Id, CancellationToken.None).GetAwaiter().GetResult();
            if (moved > 0) app.Logger.LogInformation("{Count} projecten zonder groep in de privégroep van {Username} gezet", moved, admin.Username);
        }
        return app;
    }
}

/// <summary>Turns account rule violations into a ProblemDetails answer with the right status code.</summary>
public sealed class AccountExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        if (exception is not AccountException ex) return false;
        http.Response.StatusCode = ex.Status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = { Title = ex.Message, Status = ex.Status },
        });
    }
}
