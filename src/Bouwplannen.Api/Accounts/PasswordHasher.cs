using System.Security.Cryptography;

namespace Bouwplannen.Api.Accounts;

/// <summary>
/// PBKDF2-SHA256 with a random salt (built into .NET, no packages).
/// Stored as "pbkdf2-sha256$iterations$salt$hash" so the work factor can be raised later
/// without breaking existing passwords.
/// </summary>
public static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    /// <summary>OWASP 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    public const int MinLength = 10;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>A hash of a random password, verified against when the user does not exist, so timing does not reveal valid usernames.</summary>
    public static readonly string Dummy = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));

    /// <summary>Null when acceptable, otherwise the reason in Dutch.</summary>
    public static string? Check(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"Een wachtwoord moet minstens {MinLength} tekens hebben.";
        if (password.Length > 200) return "Een wachtwoord mag maximaal 200 tekens hebben.";
        return null;
    }
}
