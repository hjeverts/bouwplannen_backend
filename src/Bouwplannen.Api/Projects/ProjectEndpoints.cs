using Bouwplannen.Api.Accounts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace Bouwplannen.Api.Projects;

/// <summary>
/// Projects, visible to the members of the project's group. A project of a group you are not in
/// behaves as if it does not exist (404), so ids of other people's projects reveal nothing.
/// </summary>
public static class ProjectEndpoints
{
    public static RouteGroupBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        // GET /api/projects                 → all projects you can see
        // GET /api/projects?since=<cursor>  → only what changed since the cursor, deletions included
        group.MapGet("/", async (DateTimeOffset? since, bool? includeDeleted, HttpContext http, IProjectRepository repo, AccountStore accounts, CancellationToken ct) =>
        {
            var access = Access.For(http, accounts);
            var cursor = await repo.CursorAsync(ct);
            var all = await repo.ListAsync(since, includeDeleted ?? false, ct);
            var projects = all.Where(access.CanSee).ToList();
            // Moved out of one of your groups into a group you are not in: tell the client to drop it.
            var removed = since is null
                ? []
                : all.Where(p => !access.CanSee(p) && p.PreviousGroupId is { } prev && access.GroupIds.Contains(prev)).Select(p => p.Id).ToList();
            return TypedResults.Ok(new ProjectListResponse(cursor, projects, access.GroupIds.ToList(), removed));
        });

        group.MapGet("/{id}", async Task<Results<Ok<ProjectDocument>, NotFound>> (string id, HttpContext http, IProjectRepository repo, AccountStore accounts, CancellationToken ct) =>
        {
            var project = await repo.GetAsync(id, ct);
            if (project is null || !Access.For(http, accounts).CanSee(project)) return TypedResults.NotFound();
            SetETag(http, project);
            return TypedResults.Ok(project);
        });

        group.MapPut("/{id}", async Task<Results<Created<ProjectDocument>, Ok<ProjectDocument>, Conflict<ProjectDocument>, NotFound, ValidationProblem, ProblemHttpResult>> (
            string id, ProjectInput input, HttpContext http, IProjectRepository repo, AccountStore accounts, CancellationToken ct) =>
        {
            var errors = ProjectRules.Validate(id, input);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
            if (!TryReadIfMatch(http, out var expected))
                return TypedResults.Problem(title: "If-Match moet een versienummer zijn, bijvoorbeeld \"3\".", statusCode: StatusCodes.Status400BadRequest);

            var access = Access.For(http, accounts);
            if (!access.GroupIds.Contains(input.GroupId!))
                return TypedResults.Problem(title: "Je bent geen lid van de gekozen groep.", statusCode: StatusCodes.Status403Forbidden);
            // Existing project (or its tombstone) of a group you cannot see: same answer as "not there",
            // but never overwrite it.
            var existing = await repo.GetIncludingDeletedAsync(id, ct);
            if (existing is not null && !access.CanSee(existing)) return TypedResults.NotFound();

            var result = await repo.UpsertAsync(id, input, expected, ct);
            switch (result.Status)
            {
                case WriteStatus.Created:
                    SetETag(http, result.Project!);
                    return TypedResults.Created($"/api/projects/{id}", result.Project!);
                case WriteStatus.Updated:
                    SetETag(http, result.Project!);
                    return TypedResults.Ok(result.Project!);
                case WriteStatus.Conflict when result.Project is not null:
                    SetETag(http, result.Project);
                    return TypedResults.Conflict(result.Project);
                default:
                    return TypedResults.NotFound();
            }
        });

        group.MapDelete("/{id}", async Task<Results<NoContent, NotFound, Conflict<ProjectDocument>, BadRequest<string>>> (
            string id, HttpContext http, IProjectRepository repo, AccountStore accounts, CancellationToken ct) =>
        {
            if (!TryReadIfMatch(http, out var expected)) return TypedResults.BadRequest("If-Match moet een versienummer zijn, bijvoorbeeld \"3\".");
            var existing = await repo.GetAsync(id, ct);
            if (existing is null || !Access.For(http, accounts).CanSee(existing)) return TypedResults.NotFound();
            var result = await repo.DeleteAsync(id, expected, ct);
            return result.Status switch
            {
                WriteStatus.Deleted => TypedResults.NoContent(),
                WriteStatus.Conflict => TypedResults.Conflict(result.Project!),
                _ => TypedResults.NotFound(),
            };
        });

        return group;
    }

    /// <summary>What the signed-in user may see: projects in their groups; admins also see projects without a group.</summary>
    private sealed record Access(HashSet<string> GroupIds, bool IsAdmin)
    {
        public static Access For(HttpContext http, AccountStore accounts)
        {
            var userId = http.User.UserId();
            var user = accounts.FindUser(userId);
            return new Access(accounts.AccessibleGroupIds(userId), user?.IsAdmin ?? false);
        }

        public bool CanSee(ProjectDocument p) => p.GroupId is null ? IsAdmin : GroupIds.Contains(p.GroupId);
    }

    private static void SetETag(HttpContext http, ProjectDocument project) =>
        http.Response.Headers.ETag = $"\"{project.Version}\"";

    /// <summary>
    /// If-Match: "3" → expect version 3; If-Match: * → must exist (-1); no header → unconditional (null).
    /// </summary>
    private static bool TryReadIfMatch(HttpContext http, out long? expected)
    {
        expected = null;
        var raw = http.Request.Headers[HeaderNames.IfMatch].ToString().Trim();
        if (raw.Length == 0) return true;
        if (raw == "*")
        {
            expected = -1;
            return true;
        }
        if (raw.StartsWith("W/", StringComparison.Ordinal)) raw = raw[2..];
        if (long.TryParse(raw.Trim('"'), out var v) && v >= 0)
        {
            expected = v;
            return true;
        }
        return false;
    }
}
