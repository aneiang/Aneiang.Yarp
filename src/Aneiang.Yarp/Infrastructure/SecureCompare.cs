using System.Security.Cryptography;
using System.Text;

namespace Aneiang.Yarp.Infrastructure;

/// <summary>
/// Constant-time comparison for secrets that arrive over the network.
/// </summary>
/// <remarks>
/// <c>a == b</c> returns as soon as two characters differ, so the time it takes measures how many
/// leading characters were right. That is not a theoretical attack for a key with a fixed prefix:
/// it turns "guess the whole key" into "guess the key one character at a time".
/// </remarks>
public static class SecureCompare
{
    /// <summary>Compares two strings without leaking where they diverge.</summary>
    public static bool Equals(string? left, string? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Length != right.Length) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    }
}
