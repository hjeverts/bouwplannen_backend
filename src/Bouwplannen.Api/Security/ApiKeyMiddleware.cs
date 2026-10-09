using System.Security.Cryptography;
using System.Text;

namespace Bouwplannen.Api.Security;

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public const string HeaderName = "X-Api-Key";

    /// <summary>
    /// Shared secret the app sends in the X-Api-Key header. Empty means no check: only for local
    /// development. Set it via an environment variable (Auth__ApiKey), not in a committed file.
    /// </summary>
    public string? ApiKey { get; set; }
}

/// <summary>The configured key as bytes; null when no key is set.</summary>
public sealed record ApiKeySecret(byte[]? Value);

/// <summary>Requires the API key on /api/* (except /api/health). Compares in constant time.</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, ApiKeySecret secret)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var protectedPath = path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/health");
        // CORS preflight requests carry no custom headers; the CORS middleware answers them.
        var preflight = HttpMethods.IsOptions(context.Request.Method);

        if (secret.Value is { } expectedKey && protectedPath && !preflight)
        {
            var given = context.Request.Headers[AuthOptions.HeaderName].ToString();
            if (!Matches(given, expectedKey))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { title = "Ontbrekende of onjuiste API-sleutel.", status = 401 });
                return;
            }
        }
        await next(context);
    }

    private static bool Matches(string given, byte[] expected)
    {
        var bytes = Encoding.UTF8.GetBytes(given);
        // FixedTimeEquals needs equal lengths; compare hashes so the key length does not leak either.
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(expected));
    }
}
