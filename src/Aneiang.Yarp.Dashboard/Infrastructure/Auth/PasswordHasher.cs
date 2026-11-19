using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>
/// Password hashing and comparison for dashboard accounts.
/// </summary>
/// <remarks>
/// <para>
/// Format: <c>pbkdf2-sha256$iterations$salt-b64$hash-b64</c>. Self-describing on purpose — the
/// iteration count has to live with the hash, otherwise raising it later silently invalidates every
/// stored credential, or worse, forces the code to guess.
/// </para>
/// <para>
/// 210,000 iterations is the OWASP figure for PBKDF2-HMAC-SHA256. It is deliberately slow: this
/// runs once per login, not once per request.
/// </para>
/// <para>
/// Plaintext <c>Password</c> is still accepted (one release) so existing deployments keep working,
/// but it is compared in constant time and logged as a warning, because the point of this type is
/// to make plaintext unnecessary rather than merely discouraged.
/// </para>
/// </remarks>
public static class PasswordHasher
{
    /// <summary>Iteration count used for newly produced hashes.</summary>
    public const int DefaultIterations = 210_000;

    private const string Prefix = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int KeySize = 32;

    /// <summary>Produces a self-describing hash string for storage in <c>PasswordHash</c>.</summary>
    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (iterations < 10_000) throw new ArgumentOutOfRangeException(nameof(iterations));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);

        return string.Join('$',
            Prefix,
            iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(key));
    }

    /// <summary>
    /// Verifies a password against a stored hash. Returns false — never throws — for a malformed
    /// hash, so one bad configuration entry cannot take the login endpoint down.
    /// </summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash)) return false;

        var parts = storedHash.Split('$');
        if (parts.Length != 4) return false;
        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations) ||
            iterations < 1)
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (expected.Length == 0) return false;

            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>True when the value looks like a hash this type produced.</summary>
    public static bool IsHash(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix + "$", StringComparison.Ordinal);

    /// <summary>
    /// Constant-time string comparison, for the plaintext fallback and for shared secrets.
    /// </summary>
    /// <remarks>
    /// Length is compared first because <see cref="CryptographicOperations.FixedTimeEquals"/> cannot
    /// compare spans of different lengths; leaking "the length is wrong" is not a useful oracle.
    /// </remarks>
    public static bool SecureEquals(string? left, string? right) =>
        Aneiang.Yarp.Infrastructure.SecureCompare.Equals(left, right);
}
