using Aneiang.Yarp.Dashboard.Infrastructure.Auth;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Tests the two properties of <see cref="TotpHelper.Validate"/> that make a second factor a factor:
/// a code that was already used cannot be used again, and the window around it stays narrow.
/// </summary>
/// <remarks>
/// Before the high-water mark existed, <c>ValidateCode</c> accepted the same six digits for up to
/// ninety seconds. Anyone who observed a code once — over a shoulder, in a screen share, in a
/// phishing proxy — could replay it from anywhere, once, and that one replay is a complete login.
/// </remarks>
public class TotpReplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Secret = TotpHelper.GenerateSecret();

    private static long Counter(DateTimeOffset at) => at.ToUnixTimeSeconds() / 30;

    [Fact]
    public void A_code_is_accepted_when_nothing_was_used_before()
    {
        var code = TotpHelper.GenerateCode(Secret, Now);

        var result = TotpHelper.Validate(Secret, code, lastAcceptedCounter: null, Now);

        Assert.True(result.Valid);
        Assert.Equal(Counter(Now), result.Counter);
    }

    [Fact]
    public void The_same_code_is_refused_once_it_has_been_used()
    {
        var code = TotpHelper.GenerateCode(Secret, Now);
        var first = TotpHelper.Validate(Secret, code, null, Now);
        Assert.True(first.Valid);

        // Same code, same step, only the remembered counter differs — which is the whole guard.
        var replay = TotpHelper.Validate(Secret, code, first.Counter, Now);

        Assert.False(replay.Valid);
    }

    [Fact]
    public void The_next_time_step_still_works_after_a_used_one()
    {
        // The guard must not lock the account out: thirty seconds later the next code is fresh.
        var first = TotpHelper.Validate(Secret, TotpHelper.GenerateCode(Secret, Now), null, Now);
        var later = Now.AddSeconds(30);
        var next = TotpHelper.Validate(Secret, TotpHelper.GenerateCode(Secret, later), first.Counter, later);

        Assert.True(next.Valid);
        Assert.True(next.Counter > first.Counter);
    }

    [Fact]
    public void A_used_code_cannot_be_replayed_from_the_previous_step()
    {
        // The ±1 window is what makes replay possible at all: after the step rolls over, the code
        // from the step before is still inside the window. The counter check has to reject it on
        // the step it belongs to, not on the current one.
        var used = TotpHelper.Validate(Secret, TotpHelper.GenerateCode(Secret, Now), null, Now);
        Assert.True(used.Valid);

        var nextStep = Now.AddSeconds(30);
        var replay = TotpHelper.Validate(Secret, TotpHelper.GenerateCode(Secret, Now), used.Counter, nextStep);

        Assert.False(replay.Valid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("      ")]
    public void Malformed_codes_are_refused_without_a_counter(string code)
    {
        var result = TotpHelper.Validate(Secret, code, null, Now);

        Assert.False(result.Valid);
        Assert.Equal(-1, result.Counter);
    }

    [Fact]
    public void A_code_from_a_different_secret_is_refused()
    {
        var code = TotpHelper.GenerateCode(Secret, Now);

        var result = TotpHelper.Validate(TotpHelper.GenerateSecret(), code, null, Now);

        Assert.False(result.Valid);
    }
}
