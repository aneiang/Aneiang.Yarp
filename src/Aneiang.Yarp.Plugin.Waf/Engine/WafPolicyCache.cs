using System.Runtime.CompilerServices;
using Aneiang.Yarp.Models;
using Microsoft.Extensions.Logging;

namespace Aneiang.Yarp.Plugin.Waf;

/// <summary>
/// Keeps one compiled <see cref="WafPolicy"/> per binding configuration instance.
/// </summary>
/// <remarks>
/// The execution plan is rebuilt whenever the configuration changes, so each
/// <see cref="WafBindingOptions"/> instance already stands for one configuration version. Keying on
/// the instance is therefore exact — no fingerprinting, no stale entries — and
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> drops the policy the moment the old plan does.
/// </remarks>
internal static class WafPolicyCache
{
    private static readonly ConditionalWeakTable<WafBindingOptions, WafPolicy> Policies = new();

    public static WafPolicy For(WafBindingOptions options, ILogger logger) =>
        Policies.GetValue(options, key => Compile(key, logger));

    private static WafPolicy Compile(WafBindingOptions options, ILogger logger)
    {
        var policy = WafPolicyCompiler.Compile(options);

        // Logged here rather than per request: a broken rule would otherwise fill the log with the
        // same complaint for every request. A concurrent first request may log twice; a duplicated
        // warning is a much smaller problem than a warning nobody ever sees.
        foreach (var warning in policy.Warnings)
            logger.LogWarning("WAF configuration: {Warning}", warning);

        return policy;
    }
}
