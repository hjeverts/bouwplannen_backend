namespace Bouwplannen.Api.Projects;

public enum WriteStatus
{
    Created,
    Updated,
    Deleted,
    NotFound,
    /// <summary>The client's expected version does not match; <see cref="WriteResult.Project"/> is the current one.</summary>
    Conflict,
}

public sealed record WriteResult(WriteStatus Status, ProjectDocument? Project);

/// <summary>
/// Storage for projects. The file-based implementation suits a single-user home server;
/// a database implementation (EF Core) can replace it without touching the endpoints.
/// </summary>
public interface IProjectRepository
{
    /// <summary>
    /// All projects. With <paramref name="since"/>: only those written after that server time,
    /// including deleted ones (tombstones) so a client can remove them locally.
    /// </summary>
    Task<IReadOnlyList<ProjectDocument>> ListAsync(DateTimeOffset? since, bool includeDeleted, CancellationToken ct);

    /// <summary>
    /// Cursor to pass as "since" next time. Every write after this call gets a later timestamp,
    /// so a client that syncs with it never misses a change.
    /// </summary>
    Task<DateTimeOffset> CursorAsync(CancellationToken ct);

    /// <summary>The project, or null when it does not exist or was deleted.</summary>
    Task<ProjectDocument?> GetAsync(string id, CancellationToken ct);

    /// <param name="expectedVersion">
    /// null: write unconditionally. 0 or higher: only if the stored version matches.
    /// -1: only if the project exists (If-Match: *).
    /// </param>
    Task<WriteResult> UpsertAsync(string id, ProjectInput input, long? expectedVersion, CancellationToken ct);

    Task<WriteResult> DeleteAsync(string id, long? expectedVersion, CancellationToken ct);
}
