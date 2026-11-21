namespace AgentHQ.Core.Auth;

/// <summary>Role names on the wire and in the database. Roles, not scopes (the recorded
/// simplification): a future scope split can subdivide these without wire changes.</summary>
public static class AuthRoles
{
    public const string Admin = "admin";
    public const string Agent = "agent";
    public const string Worker = "worker";
    public const string Ci = "ci";

    public static readonly IReadOnlyList<string> All = [Admin, Agent, Worker, Ci];

    public static bool IsValid(string role) => All.Contains(role, StringComparer.Ordinal);
}

/// <summary>
/// PBKDF2 password hashing (Rfc2898DeriveBytes, 210k iterations, per-user salt) — the recorded
/// decision: framework-free, boring, adequate. Stored format:
/// <c>pbkdf2-210k.&lt;base64 salt&gt;.&lt;base64 hash&gt;</c> so a future iteration count or
/// algorithm change can rehash on next successful login. Pure crypto, no I/O — Core.
/// </summary>
public static class PasswordHasher
{
    public const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Prefix = "pbkdf2-210k.";

    public static string Hash(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SaltSize);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt,
            Iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, HashSize);
        return Prefix + Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = stored[Prefix.Length..].Split('.');
        if (parts.Length != 2) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expected = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt,
            Iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, expected.Length);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>
/// Bearer tokens are random 256-bit values; the database stores only their SHA-256 hash
/// (same rule as passwords). The raw token is shown once, at issue time.
/// </summary>
public static class TokenGenerator
{
    public static string New() => Convert.ToBase64String(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token)));
}
