using System.Text.Json;
using System.Text.RegularExpressions;
using Bouwplannen.Api.Projects;
using Microsoft.Extensions.Options;

namespace Bouwplannen.Api.Accounts;

public sealed class AccountException(string message, int status = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Users and groups in one JSON file, {DataDirectory}/accounts.json.
///
/// Both the API and the command line tool (users …) change this file, so every change takes an
/// exclusive lock on accounts.lock, reads the latest version, applies the change and writes
/// atomically. Reads use an in-memory copy that is refreshed when the file changes on disk,
/// so an account added on the command line works without restarting the API.
/// </summary>
public sealed partial class AccountStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _file;
    private readonly string _lockFile;
    private readonly TimeProvider _time;
    private readonly object _cacheLock = new();
    private AccountsData _cache = new();
    private DateTime _cacheStamp = DateTime.MinValue;
    private long _cacheLength = -1;

    public AccountStore(IOptions<StorageOptions> options, TimeProvider time)
    {
        var dir = Path.GetFullPath(options.Value.DataDirectory);
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "accounts.json");
        _lockFile = Path.Combine(dir, "accounts.lock");
        _time = time;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,31}$")]
    private static partial Regex UsernamePattern();

    public static string NormalizeUsername(string? username) => (username ?? "").Trim().ToLowerInvariant();

    // ---------------------------------------------------------------- reading

    /// <summary>Current data (refreshed from disk when the file changed). Treat as read-only.</summary>
    public AccountsData Snapshot()
    {
        var info = new FileInfo(_file);
        lock (_cacheLock)
        {
            if (!info.Exists)
            {
                if (_cacheLength != -1) (_cache, _cacheStamp, _cacheLength) = (new AccountsData(), DateTime.MinValue, -1);
                return _cache;
            }
            if (info.LastWriteTimeUtc != _cacheStamp || info.Length != _cacheLength)
            {
                _cache = Read();
                _cacheStamp = info.LastWriteTimeUtc;
                _cacheLength = info.Length;
            }
            return _cache;
        }
    }

    private AccountsData Read()
    {
        if (!File.Exists(_file)) return new AccountsData();
        return JsonSerializer.Deserialize<AccountsData>(File.ReadAllText(_file), Json) ?? new AccountsData();
    }

    public bool HasUsers => Snapshot().Users.Count > 0;

    public UserRecord? FindUser(string id) => Snapshot().Users.FirstOrDefault(u => u.Id == id);

    public UserRecord? FindByUsername(string? username)
    {
        var name = NormalizeUsername(username);
        return Snapshot().Users.FirstOrDefault(u => u.Username == name);
    }

    public IReadOnlyList<GroupRecord> GroupsOf(string userId) =>
        Snapshot().Groups.Where(g => g.Members.Contains(userId)).OrderBy(g => g.PersonalOf is null).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public GroupRecord? FindGroup(string id) => Snapshot().Groups.FirstOrDefault(g => g.Id == id);

    public GroupRecord PersonalGroup(string userId) =>
        Snapshot().Groups.First(g => g.PersonalOf == userId);

    /// <summary>Ids of the groups whose projects this user may see and change.</summary>
    public HashSet<string> AccessibleGroupIds(string userId) =>
        Snapshot().Groups.Where(g => g.Members.Contains(userId)).Select(g => g.Id).ToHashSet(StringComparer.Ordinal);

    // ---------------------------------------------------------------- changing

    /// <summary>Apply a change under the cross-process lock, on the latest data, and save it.</summary>
    public T Update<T>(Func<AccountsData, T> change)
    {
        using var guard = AcquireLock();
        var data = Read();
        var result = change(data);
        Write(data);
        return result;
    }

    private FileStream AcquireLock()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                return new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    private void Write(AccountsData data)
    {
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Json));
        File.Move(temp, _file, overwrite: true);
        lock (_cacheLock) _cacheStamp = DateTime.MinValue; // force a reload on the next read
    }

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..20];
    private static string NewStamp() => Guid.NewGuid().ToString("N");

    /// <summary>Creates the user and their private group. The very first user is always an administrator.</summary>
    public UserRecord CreateUser(string username, string? displayName, string password, bool isAdmin)
    {
        var name = NormalizeUsername(username);
        if (!UsernamePattern().IsMatch(name))
            throw new AccountException("Een gebruikersnaam heeft 2 tot 32 tekens: kleine letters, cijfers, punt, - of _.");
        if (PasswordHasher.Check(password) is { } problem) throw new AccountException(problem);
        var hash = PasswordHasher.Hash(password);
        return Update(data =>
        {
            if (data.Users.Any(u => u.Username == name)) throw new AccountException($"Gebruiker '{name}' bestaat al.", StatusCodes.Status409Conflict);
            var now = _time.GetUtcNow();
            var user = new UserRecord
            {
                Id = NewId("u-"),
                Username = name,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim(),
                PasswordHash = hash,
                IsAdmin = isAdmin || data.Users.Count == 0,
                SecurityStamp = NewStamp(),
                Created = now,
            };
            data.Users.Add(user);
            data.Groups.Add(new GroupRecord { Id = NewId("p-"), Name = "Privé", PersonalOf = user.Id, Members = [user.Id], Owners = [user.Id], Created = now });
            return user;
        });
    }

    public void SetPassword(string userId, string password)
    {
        if (PasswordHasher.Check(password) is { } problem) throw new AccountException(problem);
        var hash = PasswordHasher.Hash(password);
        Update(data =>
        {
            var user = data.Users.FirstOrDefault(u => u.Id == userId) ?? throw new AccountException("Gebruiker niet gevonden.", 404);
            user.PasswordHash = hash;
            user.SecurityStamp = NewStamp(); // other devices must log in again
            return 0;
        });
    }

    public void ModifyUser(string userId, Action<UserRecord> change, bool newStamp = false) =>
        Update(data =>
        {
            var user = data.Users.FirstOrDefault(u => u.Id == userId) ?? throw new AccountException("Gebruiker niet gevonden.", 404);
            change(user);
            if (newStamp) user.SecurityStamp = NewStamp();
            return 0;
        });

    public GroupRecord CreateGroup(string? name, string ownerId)
    {
        var clean = CleanGroupName(name);
        return Update(data =>
        {
            var group = new GroupRecord { Id = NewId("g-"), Name = clean, Members = [ownerId], Owners = [ownerId], Created = _time.GetUtcNow() };
            data.Groups.Add(group);
            return group;
        });
    }

    public static string CleanGroupName(string? name)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length is 0 or > 60) throw new AccountException("Een groepsnaam heeft 1 tot 60 tekens.");
        return clean;
    }

    /// <summary>Change a group the acting user owns (admins may change any). Private groups cannot be changed.</summary>
    public T ChangeGroup<T>(string groupId, string actingUserId, Func<AccountsData, GroupRecord, T> change, bool allowMemberSelf = false) =>
        Update(data =>
        {
            var group = data.Groups.FirstOrDefault(g => g.Id == groupId);
            var acting = data.Users.FirstOrDefault(u => u.Id == actingUserId);
            if (group is null || acting is null || (!group.Members.Contains(actingUserId) && !acting.IsAdmin))
                throw new AccountException("Groep niet gevonden.", StatusCodes.Status404NotFound);
            if (group.PersonalOf is not null) throw new AccountException("Je privégroep kun je niet wijzigen of delen.", StatusCodes.Status400BadRequest);
            if (!allowMemberSelf && !group.Owners.Contains(actingUserId) && !acting.IsAdmin)
                throw new AccountException("Alleen een beheerder van de groep kan dit.", StatusCodes.Status403Forbidden);
            return change(data, group);
        });
}
