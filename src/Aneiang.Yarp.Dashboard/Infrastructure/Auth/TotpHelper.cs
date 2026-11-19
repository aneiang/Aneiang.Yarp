using System.Security.Cryptography;
using System.Text;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>
/// TOTP (Time-based One-Time Password) helper for 2FA.
/// Implements RFC 6238 with SHA1, 30-second period, 6 digits.
/// </summary>
public static class TotpHelper
{
    private const int PeriodSeconds = 30;
    private const int Digits = 6;

    /// <summary>Generate a TOTP code from a base32-encoded secret key.</summary>
    public static string GenerateCode(string base32Secret, DateTimeOffset? timestamp = null)
    {
        var secret = Base32Decode(base32Secret);
        var counter = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / PeriodSeconds;
        return GenerateCode(secret, counter);
    }

    /// <summary>Validate a TOTP code with a ±1 period window.</summary>
    /// <remarks>
    /// Without a replay guard this accepts the same six digits for up to 90 seconds: an observer who
    /// sees a code once can use it again, from anywhere, once. <see cref="Validate"/> is the version
    /// that actually closes that.
    /// </remarks>
    public static bool ValidateCode(string base32Secret, string code) =>
        Validate(base32Secret, code, lastAcceptedCounter: null).Valid;

    /// <summary>
    /// Validates a code and reports which time step matched, so the caller can refuse to accept that
    /// step twice.
    /// </summary>
    /// <param name="base32Secret">The account's secret.</param>
    /// <param name="code">Six digits as typed.</param>
    /// <param name="lastAcceptedCounter">Highest counter already used, or null when nothing is tracked.</param>
    /// <param name="timestamp">Test seam for the clock.</param>
    public static TotpValidation Validate(
        string base32Secret,
        string code,
        long? lastAcceptedCounter,
        DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != Digits)
            return TotpValidation.Failed;

        var now = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var secret = Base32Decode(base32Secret);

        for (var offset = -1; offset <= 1; offset++)
        {
            var counter = (now / PeriodSeconds) + offset;
            if (counter < 0) continue;

            var expected = GenerateCode(secret, counter);
            var matched = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected.PadLeft(Digits, '0')),
                Encoding.ASCII.GetBytes(code.PadLeft(Digits, '0')));

            if (!matched) continue;

            // The window is deliberate (clock skew), the reuse is not. A code that matched an
            // already-used step is refused even though it is arithmetically correct.
            if (lastAcceptedCounter is not null && counter <= lastAcceptedCounter.Value)
                return TotpValidation.Failed;

            return new TotpValidation(true, counter);
        }

        return TotpValidation.Failed;
    }

    /// <summary>Generate a new random base32 secret (32 chars = 20 bytes).</summary>
    public static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encode(bytes);
    }

    /// <summary>Build an otpauth:// URI for QR code generation.</summary>
    public static string BuildOtpAuthUri(string issuer, string account, string base32Secret)
    {
        return $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={PeriodSeconds}";
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5350:Do not use weak cryptographic algorithms",
        Justification = "RFC 6238 specifies HMAC-SHA1 for TOTP and every mainstream authenticator app assumes it; the otpauth:// URI advertises algorithm=SHA1. SHA-1 collisions do not apply to HMAC, and switching the hash would silently break existing enrolments.")]
    private static string GenerateCode(byte[] secret, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(counterBytes);

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) |
                     ((hash[offset + 1] & 0xFF) << 16) |
                     ((hash[offset + 2] & 0xFF) << 8) |
                     (hash[offset + 3] & 0xFF);
        var code = binary % (int)Math.Pow(10, Digits);
        return code.ToString().PadLeft(Digits, '0');
    }

    private static byte[] Base32Decode(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        base32 = base32.ToUpperInvariant().TrimEnd('=');
        var bytes = new byte[base32.Length * 5 / 8];
        var buffer = 0;
        var bitsLeft = 0;
        var index = 0;

        foreach (var c in base32)
        {
            var val = alphabet.IndexOf(c);
            if (val < 0) continue;
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes[index++] = (byte)(buffer >> (bitsLeft - 8));
                bitsLeft -= 8;
            }
        }

        return bytes;
    }

    private static string Base32Encode(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new StringBuilder();
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                result.Append(alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
            result.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);

        return result.ToString();
    }

}

/// <summary>Result of a TOTP check: whether it passed and which time step it belonged to.</summary>
/// <param name="Valid">True when the code matched a step newer than any previously accepted one.</param>
/// <param name="Counter">Matched time step, to be persisted as the new high-water mark.</param>
public readonly record struct TotpValidation(bool Valid, long Counter)
{
    /// <summary>The failing result — counter -1 never matches a real step.</summary>
    public static TotpValidation Failed => new(false, -1);
}
