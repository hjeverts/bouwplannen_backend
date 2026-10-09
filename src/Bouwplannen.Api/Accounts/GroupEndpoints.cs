using Bouwplannen.Api.Projects;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Bouwplannen.Api.Accounts;

/// <summary>
/// Groups for sharing projects. Anyone can create a group and becomes its owner; owners add and
/// remove members by username. Members can leave themselves. Private groups are never shared.
/// </summary>
public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var groups = app.MapGroup("/api/groups").RequireAuthorization();

        groups.MapGet("/", async (HttpContext http, AccountStore accounts, IProjectRepository projects, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var list = new List<GroupResponse>();
            foreach (var g in accounts.GroupsOf(userId)) list.Add(await Describe(g, userId, accounts, projects, ct));
            return TypedResults.Ok(list);
        });

        groups.MapPost("/", async (GroupInput input, HttpContext http, AccountStore accounts, IProjectRepository projects, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var group = accounts.CreateGroup(input.Name, userId);
            return TypedResults.Created($"/api/groups/{group.Id}", await Describe(group, userId, accounts, projects, ct));
        });

        groups.MapPut("/{id}", async (string id, GroupInput input, HttpContext http, AccountStore accounts, IProjectRepository projects, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var name = AccountStore.CleanGroupName(input.Name);
            var group = accounts.ChangeGroup(id, userId, (_, g) =>
            {
                g.Name = name;
                return g;
            });
            return TypedResults.Ok(await Describe(group, userId, accounts, projects, ct));
        });

        groups.MapDelete("/{id}", async (string id, HttpContext http, AccountStore accounts, IProjectRepository projects, CancellationToken ct) =>
        {
            var count = await projects.CountInGroupAsync(id, ct);
            accounts.ChangeGroup(id, http.User.UserId(), (data, g) =>
            {
                if (count > 0)
                    throw new AccountException($"De groep heeft nog {count} project{(count == 1 ? "" : "en")}. Verplaats of verwijder die eerst.", StatusCodes.Status409Conflict);
                data.Groups.Remove(g);
                return 0;
            });
            return TypedResults.NoContent();
        });

        groups.MapPost("/{id}/members", async (string id, MemberInput input, HttpContext http, AccountStore accounts, IProjectRepository projects, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var target = accounts.FindByUsername(input.Username);
            if (target is null || target.Disabled) throw new AccountException($"Er is geen gebruiker '{AccountStore.NormalizeUsername(input.Username)}'.", StatusCodes.Status404NotFound);
            var group = accounts.ChangeGroup(id, userId, (_, g) =>
            {
                if (!g.Members.Contains(target.Id)) g.Members.Add(target.Id);
                if (input.Owner && !g.Owners.Contains(target.Id)) g.Owners.Add(target.Id);
                if (!input.Owner && g.Owners.Contains(target.Id) && target.Id != userId)
                {
                    if (g.Owners.Count == 1) throw new AccountException("Een groep heeft minstens één beheerder nodig.");
                    g.Owners.Remove(target.Id);
                }
                return g;
            });
            return TypedResults.Ok(await Describe(group, userId, accounts, projects, ct));
        });

        // Owners remove anyone; members may remove themselves (leave the group).
        groups.MapDelete("/{id}/members/{memberId}", (string id, string memberId, HttpContext http, AccountStore accounts) =>
        {
            var userId = http.User.UserId();
            accounts.ChangeGroup(id, userId, (data, g) =>
            {
                var acting = data.Users.First(u => u.Id == userId);
                if (memberId != userId && !g.Owners.Contains(userId) && !acting.IsAdmin)
                    throw new AccountException("Alleen een beheerder van de groep kan anderen verwijderen.", StatusCodes.Status403Forbidden);
                if (!g.Members.Contains(memberId)) throw new AccountException("Deze persoon is geen lid.", StatusCodes.Status404NotFound);
                if (g.Owners.Contains(memberId) && g.Owners.Count == 1 && g.Members.Count > 1)
                    throw new AccountException("Maak eerst iemand anders beheerder van de groep.");
                g.Members.Remove(memberId);
                g.Owners.Remove(memberId);
                return 0;
            }, allowMemberSelf: true);
            return TypedResults.NoContent();
        });
    }

    private static async Task<GroupResponse> Describe(GroupRecord g, string userId, AccountStore accounts, IProjectRepository projects, CancellationToken ct)
    {
        var members = g.Members
            .Select(id => accounts.FindUser(id))
            .Where(u => u is not null)
            .Select(u => new MemberResponse(u!.Id, u.Username, u.DisplayName, g.Owners.Contains(u.Id)))
            .OrderByDescending(m => m.Owner).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var name = g.PersonalOf is not null ? "Privé" : g.Name;
        return new GroupResponse(g.Id, name, g.PersonalOf is not null, g.Owners.Contains(userId), members, await projects.CountInGroupAsync(g.Id, ct));
    }
}
