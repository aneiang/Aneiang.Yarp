using Aneiang.Yarp.Infrastructure;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Covers <see cref="SecureCompare.Equals"/> — the replacement for <c>==</c> wherever an API key or
/// password is compared (SEC-12).
/// </summary>
/// <remarks>
/// <para>
/// These tests check the answers, not the timing. Constant-time behaviour cannot be asserted
/// reliably from a unit test: the clock granularity, JIT tiering and the machine's load all move
/// individual measurements by more than the leak would. What is worth pinning down is that the
/// function is still a correct equality check — the risk with a hand-written constant-time helper is
/// that it quietly stops being one, and every comparison downstream inherits the mistake.
/// </para>
/// <para>
/// The one timing-adjacent property that <em>is</em> encoded here is the length check: different
/// lengths must return false without reading further, which is why the implementation compares
/// lengths before hashing rather than after.
/// </para>
/// </remarks>
public class SecureCompareTests
{
    [Fact]
    public void Identical_secrets_are_equal()
    {
        Assert.True(SecureCompare.Equals("correct-horse-battery-staple", "correct-horse-battery-staple"));
    }

    [Fact]
    public void Secrets_that_differ_only_in_the_last_character_are_not_equal()
    {
        // The shape a timing attack needs: right prefix, wrong tail.
        Assert.False(SecureCompare.Equals("0123456789abcdef", "0123456789abcdee"));
    }

    [Fact]
    public void Secrets_that_differ_in_length_are_not_equal()
    {
        Assert.False(SecureCompare.Equals("secret", "secret "));
    }

    [Fact]
    public void A_missing_secret_never_matches_a_present_one()
    {
        // Null on one side means "not configured". Comparing that to a supplied credential must
        // fail closed — an unset API key must not authenticate every request.
        Assert.False(SecureCompare.Equals(null, "anything"));
        Assert.False(SecureCompare.Equals("anything", null));
        Assert.False(SecureCompare.Equals(null, ""));
        Assert.False(SecureCompare.Equals("", null));
    }

    [Fact]
    public void Two_missing_secrets_are_equal()
    {
        // Both sides unconfigured is a configuration question, not an authentication one. The
        // callers check "is it configured" first; this keeps the comparison itself total.
        Assert.True(SecureCompare.Equals(null, null));
        Assert.True(SecureCompare.Equals("", ""));
    }

    [Fact]
    public void Comparison_is_ordinal_and_not_culture_sensitive()
    {
        // A culture-sensitive comparison can treat different strings as equal (or different) based
        // on the host's locale — a credential check that changes behaviour with the server locale
        // is not a check.
        Assert.False(SecureCompare.Equals("STRASSE", "straße"));
        Assert.True(SecureCompare.Equals("ABC", "ABC"));
    }

    [Fact]
    public void Non_ascii_secrets_are_compared_byte_for_byte()
    {
        Assert.True(SecureCompare.Equals("密钥-abc-123", "密钥-abc-123"));
        Assert.False(SecureCompare.Equals("密钥-abc-123", "密钥-abc-124"));
    }
}
