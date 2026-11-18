using Aneiang.Yarp.Dashboard.Modules.Alerting.Models;
using Aneiang.Yarp.Dashboard.Modules.Alerting.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Modules.Alerting.Services;

/// <summary>
/// Drives <see cref="MetricAlertService.EvaluateAsync"/> on a fixed cadence.
/// </summary>
/// <remarks>
/// A background timer rather than a hook on the request path: a rule about the last 60 seconds
/// cannot be evaluated by a request that arrives at an arbitrary moment, and evaluating on every
/// request would make the metrics endpoint the busiest thing in the gateway.
/// </remarks>
public sealed class MetricAlertSupervisor : BackgroundService
{
    /// <summary>Floor on the tick, so a typo in configuration cannot spin the CPU.</summary>
    private const int MinimumTickSeconds = 5;

    private readonly IMetricAlertService _alerts;
    private readonly MetricAlertOptions _options;
    private readonly ILogger<MetricAlertSupervisor> _logger;

    public MetricAlertSupervisor(
        IMetricAlertService alerts,
        IOptions<MetricAlertOptions> options,
        ILogger<MetricAlertSupervisor> logger)
    {
        _alerts = alerts;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tick = TimeSpan.FromSeconds(Math.Max(MinimumTickSeconds, _options.TickSeconds));

        // Let the gateway finish starting before the first pass: an empty counter snapshot taken
        // mid-boot would otherwise become the baseline every window is measured against.
        try
        {
            await Task.Delay(tick, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogDebug("Metric alert supervisor started with a {Tick}s tick", tick.TotalSeconds);

        using var timer = new PeriodicTimer(tick);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _alerts.EvaluateAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failing pass must not stop the loop: the next tick may succeed and the operator
                // would never know the alerting had silently died.
                _logger.LogWarning(ex, "Metric alert evaluation pass failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
