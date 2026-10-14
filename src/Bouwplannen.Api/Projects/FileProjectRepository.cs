using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bouwplannen.Api.Projects;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Folder for the project files. Relative paths are relative to the working directory.</summary>
    public string DataDirectory { get; set; } = "data";
}

/// <summary>
/// One JSON file per project in {DataDirectory}/projects. All projects are kept in memory as well;
/// for one person's measurements that is a few megabytes at most. Writes are serialised with a lock
/// and are atomic (write to a temporary file, then rename), so a crash never leaves half a file.
/// The files are plain JSON: easy to back up, inspect or restore by hand.
/// </summary>
public sealed class FileProjectRepository : IProjectRepository, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _dir;
    private readonly TimeProvider _time;
    private readonly ILogger<FileProjectRepository> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, ProjectDocument> _projects = new(StringComparer.Ordinal);
    private DateTimeOffset _lastStamp = DateTimeOffset.MinValue;

    public FileProjectRepository(IOptions<StorageOptions> options, TimeProvider time, ILogger<FileProjectRepository> logger)
    {
        _dir = Path.GetFullPath(Path.Combine(options.Value.DataDirectory, "projects"));
        _time = time;
        _logger = logger;
        Directory.CreateDirectory(_dir);
        LoadAll();
    }

    private void LoadAll()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(file), Json);
                if (doc is null || !ProjectRules.IsValidId(doc.Id)) continue;
                _projects[doc.Id] = doc;
                if (doc.ServerModified > _lastStamp) _lastStamp = doc.ServerModified;
            }
            catch (JsonException ex)
            {
                // Keep running: one damaged file must not take the whole service down.
                _logger.LogError(ex, "Projectbestand {File} kon niet worden gelezen en wordt overgeslagen", file);
            }
        }
        _logger.LogInformation("{Count} projecten geladen uit {Directory}", _projects.Count, _dir);
    }

    /// <summary>A strictly increasing server timestamp, so "changed since" never misses a write.</summary>
    private DateTimeOffset NextStamp()
    {
        var now = _time.GetUtcNow();
        _lastStamp = now > _lastStamp ? now : _lastStamp.AddTicks(1);
        return _lastStamp;
    }

    public async Task<IReadOnlyList<ProjectDocument>> ListAsync(DateTimeOffset? since, bool includeDeleted, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            IEnumerable<ProjectDocument> query = _projects.Values;
            if (since is { } s) query = query.Where(p => p.ServerModified > s);
            else if (!includeDeleted) query = query.Where(p => !p.Deleted);
            return query.OrderByDescending(p => p.ServerModified).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<DateTimeOffset> CursorAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return NextStamp();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ProjectDocument?> GetAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _projects.TryGetValue(id, out var p) && !p.Deleted ? p : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ProjectDocument?> GetIncludingDeletedAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _projects.GetValueOrDefault(id);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> CountInGroupAsync(string groupId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _projects.Values.Count(p => !p.Deleted && p.GroupId == groupId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> AssignOrphansAsync(string groupId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var orphans = _projects.Values.Where(p => p.GroupId is null).ToList();
            foreach (var p in orphans)
            {
                var moved = p with { GroupId = groupId, Version = p.Version + 1, ServerModified = NextStamp() };
                await WriteFileAsync(moved, ct);
                _projects[p.Id] = moved;
            }
            return orphans.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WriteResult> UpsertAsync(string id, ProjectInput input, long? expectedVersion, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _projects.TryGetValue(id, out var current);
            var exists = current is { Deleted: false };
            if (expectedVersion == -1 && !exists) return new(WriteStatus.NotFound, null);
            if (expectedVersion >= 0 && (current?.Version ?? 0) != expectedVersion) return new(WriteStatus.Conflict, exists ? current : null);

            var doc = new ProjectDocument
            {
                Id = id,
                Name = input.Name!.Trim(),
                Created = input.Created!,
                Updated = input.Updated!,
                Items = input.Items!.Value.Clone(),
                Settings = input.Settings is { ValueKind: JsonValueKind.Object } settings ? settings.Clone() : null,
                GroupId = input.GroupId,
                PreviousGroupId = current is not null && current.GroupId != input.GroupId ? current.GroupId : current?.PreviousGroupId,
                Version = (current?.Version ?? 0) + 1,
                ServerModified = NextStamp(),
                Deleted = false,
            };
            await WriteFileAsync(doc, ct);
            _projects[id] = doc;
            return new(exists ? WriteStatus.Updated : WriteStatus.Created, doc);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WriteResult> DeleteAsync(string id, long? expectedVersion, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_projects.TryGetValue(id, out var current) || current.Deleted) return new(WriteStatus.NotFound, null);
            if (expectedVersion >= 0 && current.Version != expectedVersion) return new(WriteStatus.Conflict, current);

            var tombstone = current with
            {
                Items = JsonDocument.Parse("[]").RootElement.Clone(),
                Version = current.Version + 1,
                ServerModified = NextStamp(),
                Deleted = true,
            };
            await WriteFileAsync(tombstone, ct);
            _projects[id] = tombstone;
            return new(WriteStatus.Deleted, tombstone);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WriteFileAsync(ProjectDocument doc, CancellationToken ct)
    {
        var path = Path.Combine(_dir, doc.Id + ".json");
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, doc, Json, ct);
        }
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose() => _lock.Dispose();
}
