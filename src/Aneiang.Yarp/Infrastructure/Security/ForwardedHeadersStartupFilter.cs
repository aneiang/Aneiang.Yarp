using Aneiang.Yarp.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Infrastructure.Security;

/// <summary>
/// Prepends <c>UseForwardedHeaders</c> when — and only when — trusted proxies are configured.
/// </summary>
/// <remarks>
/// <para>
/// A startup filter rather than a plain <c>app.Use...</c> call for one reason: this middleware has to
/// run before anything that reads <c>RemoteIpAddress</c> (WAF rules, IP rate limits, audit logging).
/// Asking every host to remember the call would leave the misconfiguration silent in exactly the
/// deployments that need it, and ordering it in the wrong place would look like it worked.
/// </para>
/// <para>
/// Nothing is installed when the configuration is empty, so the out-of-the-box behaviour stays
/// "believe the socket, ignore the headers".
/// </para>
/// </remarks>
internal sealed class ForwardedHeadersStartupFilter : IStartupFilter
{
    private readonly IOptions<TrustedProxyOptions> _options;
    private readonly ILogger<ForwardedHeadersStartupFilter> _logger;

    public ForwardedHeadersStartupFilter(
        IOptions<TrustedProxyOptions> options,
        ILogger<ForwardedHeadersStartupFilter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var forwarded = TrustedProxyConfigurator.Build(_options.Value, out var warning);

        if (warning is not null)
            _logger.LogWarning("Trusted proxy configuration: {Warning}", warning);

        if (forwarded is not null)
        {
            // The count is the log line that answers "did my configuration take effect" without
            // anyone having to read the config file back.
            _logger.LogInformation(
                "Forwarded headers enabled for {Proxies} trusted proxy address(es) and {Networks} trusted network(s), ForwardLimit=1.",
                forwarded.KnownProxies.Count,
                forwarded.KnownNetworks.Count);

            app.UseForwardedHeaders(forwarded);
        }

        next(app);
    };
}
