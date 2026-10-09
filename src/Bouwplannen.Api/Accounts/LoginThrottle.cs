using System.Collections.Concurrent;

namespace Bouwplannen.Api.Accounts;

/// <summary>
/// Slows down password guessing per username: after 5 failed attempts the account is locked for
/// 30 seconds, doubling with every further failure (max 15 minutes). A successful login resets it.
/// Kept in memory: a restart clears it, which is fine for this purpose. The per-IP rate limiter
/// in front of the login endpoint covers trying many usernames.
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public const int FreeAttempts = 5;
    private static readonly TimeSpan BaseLock = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);

    private sealed record Entry(int Failures, DateTimeOffset LockedUntil);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>How long this username must still wait, or null when it may try now.</summary>
    public TimeSpan? Wait(string username)
    {
        if (!_entries.TryGetValue(username, out var e)) return null;
        var left = e.LockedUntil - time.GetUtcNow();
        return left > TimeSpan.Zero ? left : null;
    }

    public void Failed(string username) =>
        _entries.AddOrUpdate(username,
            _ => new Entry(1, DateTimeOffset.MinValue),
            (_, e) =>
            {
                var failures = e.Failures + 1;
                if (failures < FreeAttempts) return new Entry(failures, DateTimeOffset.MinValue);
                var factor = Math.Pow(2, Math.Min(failures - FreeAttempts, 10));
                var lockFor = TimeSpan.FromTicks((long)Math.Min(BaseLock.Ticks * factor, MaxLock.Ticks));
                return new Entry(failures, time.GetUtcNow() + lockFor);
            });

    public void Succeeded(string username) => _entries.TryRemove(username, out _);
}
