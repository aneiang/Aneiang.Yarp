using Aneiang.Yarp.Dashboard.Modules.Canary.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Modules.Canary.Services;

/// <summary>
/// Closes expired canary observation windows on a fixed cadence.
/// </summary>
/// <remarks>
/// All the decision logic lives in <see cref="CanaryRolloutService.TickAsync"/>, which takes no
/// timer and needs no clock. This class exists only to call it, so that "did it roll back?" can be
/// answered by a unit test rather than by waiting two minutes.
/// </remarks>
public sealed class CanaryRolloutSupervisor : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly CanaryOptions _options;
    private readonly ILogger<CanaryRolloutSupervisor> _logger;

    public CanaryRolloutSupervisor(
        IServiceProvider services,
        IOptions<CanaryOptions> options,
        ILogger<CanaryRolloutSupervisor> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.TickSeconds, 1, 60));

        // Let the host finish starting (config load, plugin domain) before the first evaluation.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;

                var service = _services.GetService<ICanaryRolloutService>();
                if (service is null) continue;

                await service.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A canary supervisor that dies takes the safety net with it, and nothing would
                // report the silence. Log and keep ticking.
                _logger.LogError(ex, "Canary supervisor tick failed");
            }
        }
    }
}
