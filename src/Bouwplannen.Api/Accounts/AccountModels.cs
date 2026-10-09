namespace Bouwplannen.Api.Accounts;

public sealed class UserRecord
{
    public required string Id { get; init; }
    /// <summary>Lower case, unique. Used to log in and to add someone to a group.</summary>
    public required string Username { get; init; }
    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsAdmin { get; set; }
    public bool Disabled { get; set; }
    /// <summary>Changes on password change, "log out everywhere" and disabling: invalidates all existing logins.</summary>
    public required string SecurityStamp { get; set; }
    public DateTimeOffset Created { get; init; }
}

public sealed class GroupRecord
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    /// <summary>Set for a user's private group: only that user is ever a member.</summary>
    public string? PersonalOf { get; init; }
    /// <summary>All members, owners included.</summary>
    public List<string> Members { get; init; } = [];
    /// <summary>Members who may rename the group and add or remove members.</summary>
    public List<string> Owners { get; init; } = [];
    public DateTimeOffset Created { get; init; }
}

public sealed class AccountsData
{
    public List<UserRecord> Users { get; init; } = [];
    public List<GroupRecord> Groups { get; init; } = [];
}

// ---- API shapes ----

public sealed record LoginInput(string? Username, string? Password);

public sealed record PasswordInput(string? Current, string? New);

public sealed record GroupInput(string? Name);

public sealed record MemberInput(string? Username, bool Owner = false);

public sealed record GroupSummary(string Id, string Name, bool Personal, bool IsOwner, int MemberCount);

public sealed record MeResponse(string Id, string Username, string DisplayName, bool IsAdmin, string PersonalGroupId, IReadOnlyList<GroupSummary> Groups);

public sealed record MemberResponse(string Id, string Username, string DisplayName, bool Owner);

public sealed record GroupResponse(string Id, string Name, bool Personal, bool IsOwner, IReadOnlyList<MemberResponse> Members, int ProjectCount);
