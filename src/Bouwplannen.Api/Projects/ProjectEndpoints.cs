using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;

namespace Bouwplannen.Api.Projects;

public static class ProjectEndpoints
{
    public static RouteGroupBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");

        // GET /api/projects                 → all projects
        // GET /api/projects?since=<cursor>  → only what changed since the cursor, deletions included
        group.MapGet("/", async (DateTimeOffset? since, bool? includeDeleted, IProjectRepository repo, CancellationToken ct) =>
        {
            var cursor = await repo.CursorAsync(ct);
            var projects = await repo.ListAsync(since, includeDeleted ?? false, ct);
            return TypedResults.Ok(new ProjectListResponse(cursor, projects));
        });

        group.MapGet("/{id}", async Task<Results<Ok<ProjectDocument>, NotFound>> (string id, HttpContext http, IProjectRepository repo, CancellationToken ct) =>
        {
            var project = await repo.GetAsync(id, ct);
            if (project is null) return TypedResults.NotFound();
            SetETag(http, project);
            return TypedResults.Ok(project);
        });

        group.MapPut("/{id}", async Task<Results<Created<ProjectDocument>, Ok<ProjectDocument>, Conflict<ProjectDocument>, NotFound, ValidationProblem, BadRequest<string>>> (
            string id, ProjectInput input, HttpContext http, IProjectRepository repo, CancellationToken ct) =>
        {
            var errors = ProjectRules.Validate(id, input);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
            if (!TryReadIfMatch(http, out var expected)) return TypedResults.BadRequest("If-Match moet een versienummer zijn, bijvoorbeeld \"3\".");

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
            string id, HttpContext http, IProjectRepository repo, CancellationToken ct) =>
        {
            if (!TryReadIfMatch(http, out var expected)) return TypedResults.BadRequest("If-Match moet een versienummer zijn, bijvoorbeeld \"3\".");
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
