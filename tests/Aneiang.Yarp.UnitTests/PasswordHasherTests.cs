using Aneiang.Yarp.Dashboard.Infrastructure.Auth;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// SEC-04: credentials are stored as PBKDF2 hashes and compared in constant time. The legacy
/// plaintext field still works, but it is a fallback rather than the design.
/// </summary>
public sealed class PasswordHasherTests
{
    [Fact]
    public void A_hash_verifies_and_a_wrong_password_does_not()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHasher.Verify("correct horse battery stapl", hash));
        Assert.False(PasswordHasher.Verify("", hash));
    }

    [Fact]
    public void The_same_password_produces_different_hashes()
    {
        // A shared salt would make two accounts with the same password visibly identical in config.
        var first = PasswordHasher.Hash("same-password");
        var second = PasswordHasher.Hash("same-password");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("same-password", first));
        Assert.True(PasswordHasher.Verify("same-password", second));
    }

    [Fact]
    public void The_iteration_count_travels_with_the_hash()
    {
        // Raising the default later must not invalidate existing credentials.
        var hash = PasswordHasher.Hash("pw", iterations: 12_000);

        Assert.StartsWith("pbkdf2-sha256$12000$", hash, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify("pw", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("pbkdf2-sha256$abc$salt$hash")]
    [InlineData("pbkdf2-sha256$210000$not-base64$also-not")]
    [InlineData("md5$1$x$y")]
    public void A_malformed_hash_is_refused_rather_than_throwing(string stored)
    {
        Assert.False(PasswordHasher.Verify("pw", stored));
    }

    [Fact]
    public void IsHash_only_recognises_the_produced_format()
    {
        Assert.True(PasswordHasher.IsHash(PasswordHasher.Hash("pw")));
        Assert.False(PasswordHasher.IsHash("demo123"));
        Assert.False(PasswordHasher.IsHash(null));
        Assert.False(PasswordHasher.IsHash(""));
    }

    [Fact]
    public void The_default_iteration_count_is_the_documented_one()
    {
        // A silent drop here would weaken every new credential without any visible change.
        Assert.Equal(210_000, PasswordHasher.DefaultIterations);
        Assert.StartsWith("pbkdf2-sha256$210000$", PasswordHasher.Hash("pw"), StringComparison.Ordinal);
    }

    [Fact]
    public void SecureEquals_matches_equal_strings_and_rejects_different_ones()
    {
        Assert.True(PasswordHasher.SecureEquals("abc", "abc"));
        Assert.False(PasswordHasher.SecureEquals("abc", "abd"));
        Assert.False(PasswordHasher.SecureEquals("abc", "abcd"));
    }

    [Fact]
    public void SecureEquals_treats_two_nulls_as_equal_and_one_as_not()
    {
        Assert.True(PasswordHasher.SecureEquals(null, null));
        Assert.False(PasswordHasher.SecureEquals(null, "x"));
        Assert.False(PasswordHasher.SecureEquals("x", null));
    }
}
