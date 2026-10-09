using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bouwplannen.Api.Projects;

/// <summary>
/// A project as stored on the server. The measurements (<see cref="Items"/>) are owned by the
/// frontend and stored as-is: the server does not need to understand them to keep them safe.
/// </summary>
public sealed record ProjectDocument
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Client timestamps (ISO 8601), stored exactly as sent.</summary>
    public required string Created { get; init; }
    public required string Updated { get; init; }

    public required JsonElement Items { get; init; }

    /// <summary>Increases by one on every write; used for optimistic concurrency (ETag / If-Match).</summary>
    public long Version { get; init; }

    /// <summary>Server time of the last write, used by clients to fetch only what changed.</summary>
    public DateTimeOffset ServerModified { get; init; }

    /// <summary>Deleted projects are kept as tombstones so other devices learn about the deletion.</summary>
    public bool Deleted { get; init; }
}

/// <summary>Body of PUT /api/projects/{id}.</summary>
public sealed record ProjectInput
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Created { get; init; }
    public string? Updated { get; init; }
    public JsonElement? Items { get; init; }
}

public sealed record ProjectListResponse(DateTimeOffset ServerTime, IReadOnlyList<ProjectDocument> Projects);

public static partial class ProjectRules
{
    public const int MaxNameLength = 200;
    public const int MaxIdLength = 64;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Returns validation errors per field; empty when the input is fine.</summary>
    public static Dictionary<string, string[]> Validate(string routeId, ProjectInput input)
    {
        var errors = new Dictionary<string, string[]>();
        if (!IsValidId(routeId))
            errors["id"] = ["Id mag alleen letters, cijfers, - en _ bevatten (maximaal 64 tekens)."];
        else if (input.Id is not null && input.Id != routeId)
            errors["id"] = ["Id in de body hoort gelijk te zijn aan het id in de URL."];

        if (string.IsNullOrWhiteSpace(input.Name))
            errors["name"] = ["Naam is verplicht."];
        else if (input.Name.Length > MaxNameLength)
            errors["name"] = [$"Naam mag maximaal {MaxNameLength} tekens zijn."];

        if (!IsTimestamp(input.Created)) errors["created"] = ["Created moet een ISO 8601-tijdstip zijn."];
        if (!IsTimestamp(input.Updated)) errors["updated"] = ["Updated moet een ISO 8601-tijdstip zijn."];

        if (input.Items is not { ValueKind: JsonValueKind.Array })
            errors["items"] = ["Items moet een lijst (JSON-array) zijn."];

        return errors;
    }

    private static bool IsTimestamp(string? value) =>
        value is not null && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out _);
}
