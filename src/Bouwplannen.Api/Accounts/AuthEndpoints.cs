using System.Security.Claims;
using Bouwplannen.Api.Projects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bouwplannen.Api.Accounts;

public static class ClaimsExtensions
{
    public const string StampClaim = "stamp";

    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Niet ingelogd.");
}

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapPost("/login", async Task<Results<Ok<MeResponse>, ProblemHttpResult>> (
            LoginInput input, HttpContext http, AccountStore accounts, LoginThrottle throttle, IProjectRepository projects, ILoggerFactory logs) =>
        {
            var username = AccountStore.NormalizeUsername(input.Username);
            if (throttle.Wait(username) is { } wait)
            {
                http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
                return TypedResults.Problem(title: $"Te veel mislukte pogingen. Probeer het over {Math.Ceiling(wait.TotalSeconds)} seconden opnieuw.", statusCode: StatusCodes.Status429TooManyRequests);
            }

            var user = accounts.FindByUsername(username);
            // Always run the hash, also for unknown users, so the answer time does not reveal which names exist.
            var ok = PasswordHasher.Verify(input.Password ?? "", user?.PasswordHash ?? PasswordHasher.Dummy);
            if (user is null || !ok || user.Disabled)
            {
                throttle.Failed(username);
                logs.CreateLogger("Login").LogWarning("Mislukte login voor {Username} vanaf {Ip}", username, http.Connection.RemoteIpAddress);
                return TypedResults.Problem(title: "Gebruikersnaam of wachtwoord klopt niet.", statusCode: StatusCodes.Status401Unauthorized);
            }
            throttle.Succeeded(username);

            if (user.IsAdmin)
            {
                // Projects from before accounts existed go to the first administrator who logs in.
                var moved = await projects.AssignOrphansAsync(accounts.PersonalGroup(user.Id).Id, http.RequestAborted);
                if (moved > 0) logs.CreateLogger("Login").LogInformation("{Count} projecten zonder groep in de privégroep van {Username} gezet", moved, user.Username);
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, user.Id),
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimsExtensions.StampClaim, user.SecurityStamp),
                ],
                CookieAuthenticationDefaults.AuthenticationScheme));
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = true });
            return TypedResults.Ok(Me(accounts, user));
        }).RequireRateLimiting(BouwplannenApp.LoginRateLimit);

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.NoContent();
        });

        auth.MapGet("/me", Results<Ok<MeResponse>, UnauthorizedHttpResult> (HttpContext http, AccountStore accounts) =>
        {
            var user = accounts.FindUser(http.User.UserId());
            return user is null ? TypedResults.Unauthorized() : TypedResults.Ok(Me(accounts, user));
        }).RequireAuthorization();

        // Logs out all devices of this user (including this one).
        auth.MapPost("/logout-all", async (HttpContext http, AccountStore accounts) =>
        {
            accounts.ModifyUser(http.User.UserId(), _ => { }, newStamp: true);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.NoContent();
        }).RequireAuthorization();

        auth.MapPost("/password", Results<NoContent, ProblemHttpResult> (PasswordInput input, HttpContext http, AccountStore accounts) =>
        {
            var user = accounts.FindUser(http.User.UserId())!;
            if (!PasswordHasher.Verify(input.Current ?? "", user.PasswordHash))
                return TypedResults.Problem(title: "Je huidige wachtwoord klopt niet.", statusCode: StatusCodes.Status400BadRequest);
            accounts.SetPassword(user.Id, input.New ?? "");
            // The new security stamp logs out every device; sign this one back in is left to the client.
            return TypedResults.NoContent();
        }).RequireAuthorization();
    }

    public static MeResponse Me(AccountStore accounts, UserRecord user)
    {
        var groups = accounts.GroupsOf(user.Id)
            .Select(g => new GroupSummary(g.Id, g.Name, g.PersonalOf is not null, g.Owners.Contains(user.Id), g.Members.Count))
            .ToList();
        return new MeResponse(user.Id, user.Username, user.DisplayName, user.IsAdmin, accounts.PersonalGroup(user.Id).Id, groups);
    }
}
