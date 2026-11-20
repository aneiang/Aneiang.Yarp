using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>
/// Manages JWT signing key lifecycle: generates, persists, and retrieves the secret.
/// Ensures the signing key survives application restarts so issued tokens remain valid.
/// </summary>
public sealed class JwtSecretProvider
{
    private readonly string _filePath;
    private readonly ILogger<JwtSecretProvider> _logger;
    private string? _cachedSecret;

    public JwtSecretProvider(IWebHostEnvironment env, ILogger<JwtSecretProvider> logger)
    {
        _logger = logger;
        var dataDir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDir);
        _filePath = Path.Combine(dataDir, ".jwt-secret");
    }

    /// <summary>
    /// Returns the JWT signing secret.
    /// Priority: configured value → persisted file → auto-generated and persisted.
    /// </summary>
    /// <param name="configuredSecret">Secret from configuration (null means "auto-generate if needed").</param>
    /// <returns>The active JWT signing secret.</returns>
    public string GetSecret(string? configuredSecret)
    {
        if (_cachedSecret != null)
            return _cachedSecret;

        if (!string.IsNullOrWhiteSpace(configuredSecret))
        {
            _cachedSecret = configuredSecret;
            _logger.LogDebug("Using JWT secret from configuration");
            return _cachedSecret;
        }

        _cachedSecret = LoadOrCreateSecret();
        return _cachedSecret;
    }

    private string LoadOrCreateSecret()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("secret", out var secretProp))
                {
                    var secret = secretProp.GetString();
                    if (!string.IsNullOrWhiteSpace(secret))
                    {
                        _logger.LogDebug("JWT secret loaded from {File}", _filePath);

                        // A file written by an earlier version (or copied in by an operator) may
                        // still be world-readable. Tightening it on read means the fix applies to
                        // existing deployments without anyone having to chmod by hand.
                        RestrictToOwner();

                        return secret;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read JWT secret file, generating a new one");
            }
        }

        var newSecret = GenerateSecret();
        PersistSecret(newSecret);
        _logger.LogWarning(
            "JWT secret auto-generated and persisted to {File}. Tokens signed with the previous key are no " +
            "longer accepted. Set Gateway:Dashboard:JwtSecret explicitly in configuration for production.",
            _filePath);
        return newSecret;
    }

    private void PersistSecret(string secret)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { secret, generatedAt = DateTime.Now });
            File.WriteAllText(_filePath, payload);
            RestrictToOwner();
            _logger.LogDebug("JWT secret persisted to {File}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist JWT secret to {File} — tokens will not survive restart", _filePath);
        }
    }

    /// <summary>
    /// Restricts the secret file to owner read/write on Unix.
    /// </summary>
    /// <remarks>
    /// Anyone who can read this file can mint a dashboard session for any role, so the default
    /// umask mode (0644 on most shared hosts, 0664 with a permissive umask) is too open. Windows
    /// is left to the inherited ACL of the content root: emulating POSIX bits with an ACL edit here
    /// would more likely break the deployment than harden it, and that asymmetry is documented
    /// rather than silently skipped.
    /// </remarks>
    private void RestrictToOwner()
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Not fatal: the secret still works, it is just readable more widely than intended.
            // Saying so is the difference between a hardening gap and an invisible one.
            _logger.LogWarning(ex,
                "Could not restrict {File} to owner-only permissions; the JWT signing key may be readable by other users.", _filePath);
        }
    }

    private static string GenerateSecret()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
