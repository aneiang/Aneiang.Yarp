using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>JWT token generation and validation (zero external dependency).</summary>
public static class DashboardJwtHelper
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>The only issuer this gateway accepts.</summary>
    public const string Issuer = "Aneiang.Yarp.Dashboard";

    /// <summary>Token lifetime. Kept here so the cookie expiry and the claim cannot drift apart.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    /// <summary>Generate a signed JWT token.</summary>
    /// <param name="username">Subject claim.</param>
    /// <param name="secret">HMAC-SHA256 signing key.</param>
    /// <param name="role">
    /// Role claim. Carried in the token so the authorization filter does not have to look the
    /// account up again on every request — and so a role change requires a new login, which is the
    /// behaviour you want for a privilege reduction.
    /// </param>
    public static string GenerateToken(string username, string secret, DashboardRole? role = null)
    {
        var header = JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" }, _jsonOptions);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = JsonSerializer.Serialize(new
        {
            sub = username,
            iss = Issuer,
            role = role?.ToClaimValue(),
            // The id is what makes "log out this session" expressible: a signature cannot be
            // un-signed, so the only way to invalidate a token early is to remember its id.
            jti = Guid.NewGuid().ToString("N"),
            iat = now,
            exp = now + (long)Lifetime.TotalSeconds
        }, _jsonOptions);

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(header));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signingInput = $"{headerB64}.{payloadB64}";

        var signature = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
            .ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var signatureB64 = Base64UrlEncode(signature);

        return $"{headerB64}.{payloadB64}.{signatureB64}";
    }

    /// <summary>Validate a signed JWT token.</summary>
    /// <param name="token">The JWT string.</param>
    /// <param name="secret">HMAC-SHA256 signing key.</param>
    /// <returns>(valid, username) tuple.</returns>
    public static (bool Valid, string? Username) ValidateToken(string token, string secret)
    {
        var details = ValidateTokenDetails(token, secret);
        return (details.Valid, details.Username);
    }

    /// <summary>
    /// Validate a token and read its role claim.
    /// </summary>
    /// <returns>
    /// <c>Role</c> is null for tokens issued before roles existed; callers must decide what an
    /// unlabelled token means rather than defaulting it to admin here.
    /// </returns>
    public static (bool Valid, string? Username, DashboardRole? Role) ValidateTokenDetails(string token, string secret)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return (false, null, null);

        // The header is part of the signed input, so an attacker cannot swap it without breaking
        // the signature - but "cannot" only holds if the algorithm is actually checked. Accepting
        // whatever the header says is the classic JWT confusion bug.
        if (!HasExpectedHeader(parts[0])) return (false, null, null);

        var signingInput = $"{parts[0]}.{parts[1]}";
        var expectedSig = ComputeSignature(signingInput, secret);

        byte[] actualSig;
        byte[] payloadBytes;
        try
        {
            actualSig = Base64UrlDecode(parts[2]);
            payloadBytes = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            // Malformed base64url payload/signature — treat as invalid, never throw.
            return (false, null, null);
        }

        if (!ConstantTimeEquals(actualSig, expectedSig))
            return (false, null, null);

        try
        {
            using var doc = JsonDocument.Parse(payloadBytes);
            var root = doc.RootElement;

            // Check expiry
            if (root.TryGetProperty("exp", out var expEl))
            {
                var expTime = DateTimeOffset.FromUnixTimeSeconds(expEl.GetInt64());
                if (expTime < DateTimeOffset.UtcNow) return (false, null, null);
            }

            // The issuer is generated but was never checked, which made it decoration. A token
            // signed with the same secret by another system would otherwise be accepted here.
            if (!root.TryGetProperty("iss", out var issEl) ||
                !string.Equals(issEl.GetString(), Issuer, StringComparison.Ordinal))
                return (false, null, null);

            var username = root.TryGetProperty("sub", out var subEl) ? subEl.GetString() : null;

            DashboardRole? role = null;
            if (root.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
                role = DashboardRoles.Parse(roleEl.GetString(), DashboardRole.Admin, out _);

            return (true, username, role);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or KeyNotFoundException)
        {
            // Invalid or unexpected payload structure — treat as invalid, never throw.
            return (false, null, null);
        }
    }

    /// <summary>Reads the <c>jti</c> claim of an already validated token.</summary>
    public static string? ReadTokenId(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            return doc.RootElement.TryGetProperty("jti", out var jti) ? jti.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static bool HasExpectedHeader(string headerB64)
    {
        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(headerB64));
            var root = doc.RootElement;

            var alg = root.TryGetProperty("alg", out var algEl) ? algEl.GetString() : null;
            if (!string.Equals(alg, "HS256", StringComparison.Ordinal)) return false;

            // typ is optional in the RFC; when it is present it has to be JWT.
            if (root.TryGetProperty("typ", out var typEl) && typEl.ValueKind == JsonValueKind.String &&
                !string.Equals(typEl.GetString(), "JWT", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] ComputeSignature(string input, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var b64 = input.Replace('-', '+').Replace('_', '/');
        var pad = b64.Length % 4;
        if (pad == 2) b64 += "==";
        else if (pad == 3) b64 += "=";
        return Convert.FromBase64String(b64);
    }

    /// <summary>Constant-time comparison to prevent timing attacks.</summary>
    private static bool ConstantTimeEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
