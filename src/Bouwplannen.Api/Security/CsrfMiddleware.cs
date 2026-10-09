namespace Bouwplannen.Api.Security;

/// <summary>
/// Protection against cross-site requests riding on the login cookie: every changing request
/// (POST, PUT, DELETE) to /api must carry the header "X-Requested-With: bouwplannen". A page on
/// another site cannot add that header without a CORS preflight, which this API does not allow.
/// The cookie is also SameSite=Strict; this is the second lock on the door.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "bouwplannen";

    public async Task InvokeAsync(HttpContext context)
    {
        var method = context.Request.Method;
        var changes = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsDelete(method) || HttpMethods.IsPatch(method);
        if (changes && context.Request.Path.StartsWithSegments("/api") &&
            context.Request.Headers[HeaderName].ToString() != HeaderValue)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { title = $"Verzoek geweigerd: de header {HeaderName} ontbreekt.", status = 403 });
            return;
        }
        await next(context);
    }
}
