using Aneiang.Yarp.Dashboard.Infrastructure.Security;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// SEC-06: the login endpoint needs a budget. These tests pin both keys — an account that is being
/// guessed, and an address that is doing the guessing.
/// </summary>
public sealed class LoginThrottleTests
{
    private static LoginThrottle Throttle(int maxFailures = 5) =>
        new() { MaxFailures = maxFailures, Window = TimeSpan.FromMinutes(5) };

    [Fact]
    public void Nothing_is_blocked_before_the_budget_is_spent()
    {
        var throttle = Throttle();

        for (var i = 0; i < 4; i++) throttle.RegisterFailure("10.0.0.1", "admin");

        Assert.Null(throttle.RetryAfter("10.0.0.1", "admin"));
    }

    [Fact]
    public void The_sixth_attempt_is_refused_with_a_wait_time()
    {
        var throttle = Throttle();

        for (var i = 0; i < 5; i++) throttle.RegisterFailure("10.0.0.1", "admin");

        var retryAfter = throttle.RetryAfter("10.0.0.1", "admin");

        Assert.NotNull(retryAfter);
        Assert.True(retryAfter!.Value > TimeSpan.Zero);
        Assert.True(retryAfter.Value <= TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void One_address_spraying_many_accounts_runs_out_of_budget()
    {
        // Per-account counting alone would let this through, one guess per account.
        var throttle = Throttle();

        for (var i = 0; i < 5; i++) throttle.RegisterFailure("10.0.0.1", "user" + i);

        Assert.NotNull(throttle.RetryAfter("10.0.0.1", "user99"));
    }

    [Fact]
    public void One_account_attacked_from_many_addresses_runs_out_of_budget()
    {
        // The mirror case: counting only addresses lets a botnet take 5 guesses per host, forever.
        var throttle = Throttle();

        for (var i = 0; i < 5; i++) throttle.RegisterFailure("10.0.0." + i, "admin");

        Assert.NotNull(throttle.RetryAfter("10.0.0.99", "admin"));
    }

    [Fact]
    public void A_successful_login_clears_the_budget()
    {
        var throttle = Throttle();

        for (var i = 0; i < 5; i++) throttle.RegisterFailure("10.0.0.1", "admin");
        Assert.NotNull(throttle.RetryAfter("10.0.0.1", "admin"));

        throttle.RegisterSuccess("10.0.0.1", "admin");

        Assert.Null(throttle.RetryAfter("10.0.0.1", "admin"));
        Assert.Equal(0, throttle.FailuresFor("10.0.0.1", "admin"));
    }

    [Fact]
    public async Task The_budget_resets_after_the_window()
    {
        var throttle = new LoginThrottle { MaxFailures = 2, Window = TimeSpan.FromMilliseconds(120) };

        throttle.RegisterFailure("10.0.0.1", "admin");
        throttle.RegisterFailure("10.0.0.1", "admin");
        Assert.NotNull(throttle.RetryAfter("10.0.0.1", "admin"));

        await Task.Delay(200);

        Assert.Null(throttle.RetryAfter("10.0.0.1", "admin"));
    }

    [Fact]
    public void The_account_key_is_case_insensitive()
    {
        // "Admin" and "admin" are the same account; counting them separately would double the budget.
        var throttle = Throttle(2);

        throttle.RegisterFailure("10.0.0.1", "ADMIN");
        throttle.RegisterFailure("10.0.0.1", "admin");

        Assert.NotNull(throttle.RetryAfter("10.0.0.2", "Admin"));
    }

    [Fact]
    public void A_missing_address_still_counts_the_account()
    {
        var throttle = Throttle(2);

        throttle.RegisterFailure(null, "admin");
        throttle.RegisterFailure(null, "admin");

        Assert.NotNull(throttle.RetryAfter(null, "admin"));
    }
}
