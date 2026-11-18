using Aneiang.Yarp.Dashboard.Modules.Alerting.Models;
using Aneiang.Yarp.Dashboard.Modules.Alerting.Services;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Modules.Alerting.Controllers;

/// <summary>
/// API for metric threshold rules and the incidents they produced.
/// </summary>
/// <remarks>
/// Not wired into the <c>X-Config-Version</c> guard: alert rules are runtime operational settings,
/// not part of the proxy configuration, and they change while the config editor is open.
/// </remarks>
[Route("api/alerts")]
[ApiController]
public sealed class AlertsController : ControllerBase
{
    private readonly IMetricAlertService _alerts;
    private readonly IDynamicYarpConfigService _config;
    private readonly MetricAlertStore _store;
    private readonly MetricAlertOptions _options;
    private readonly IWebhookSettingsRepository _webhookSettings;
    private readonly TimeProvider _time;

    public AlertsController(
        IMetricAlertService alerts,
        IDynamicYarpConfigService config,
        MetricAlertStore store,
        IOptions<MetricAlertOptions> options,
        IWebhookSettingsRepository webhookSettings,
        TimeProvider? timeProvider = null)
    {
        _alerts = alerts;
        _config = config;
        _store = store;
        _options = options.Value;
        _webhookSettings = webhookSettings;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Rules, incidents and everything the editor needs to render itself.</summary>
    [HttpGet]
    public async Task<IActionResult> ListAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();

        return Ok(new
        {
            code = 200,
            data = new
            {
                rules = _alerts.GetRules().Select(ToRuleDto).ToList(),
                incidents = _alerts.GetIncidents().Select(i => ToIncidentDto(i, now)).ToList(),
                storePath = _store.Path,
                eventType = MetricAlertService.NotificationEventType,
                tickSeconds = _options.TickSeconds,
                delivery = await DescribeDeliveryAsync(ct),
                kinds = KindCatalog(),
                scopes = ScopeCatalog(),
                defaults = new
                {
                    windowSeconds = _options.DefaultWindowSeconds,
                    forSeconds = _options.DefaultForSeconds,
                    minSamples = _options.DefaultMinSamples,
                    errorRateThreshold = _options.DefaultErrorRateThreshold,
                    latencyP99Seconds = _options.DefaultLatencyP99Seconds
                }
            }
        });
    }

    /// <summary>
    /// Whether anyone is actually listening for <c>MetricAlert</c>.
    /// </summary>
    /// <remarks>
    /// A rule that evaluates correctly and notifies nobody is indistinguishable from a healthy
    /// gateway. Reporting the subscription state here lets the panel say "this alert will be
    /// delivered nowhere" instead of leaving the operator to discover it during an outage.
    /// </remarks>
    private async Task<object> DescribeDeliveryAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _webhookSettings.LoadAsync(ct);
            var subscribed = settings.Platforms.Keys
                .Where(platform =>
                {
                    var events = settings.PlatformEvents.TryGetValue(platform, out var own)
                        ? own
                        : settings.EnabledEvents;
                    return events.Contains(MetricAlertService.NotificationEventType, StringComparer.OrdinalIgnoreCase);
                })
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();

            return new
            {
                configuredPlatforms = settings.Platforms.Count,
                subscribedPlatforms = subscribed,
                willDeliver = subscribed.Count > 0
            };
        }
        catch
        {
            // Reporting status must not break the page that reports it.
            return new
            {
                configuredPlatforms = 0,
                subscribedPlatforms = new List<string>(),
                willDeliver = false
            };
        }
    }

    /// <summary>Creates a rule, or replaces the one whose id is supplied.</summary>
    [HttpPost("rules")]
    public async Task<IActionResult> Save([FromBody] MetricAlertRule request, CancellationToken ct)
    {
        var result = await _alerts.SaveRuleAsync(request, ResolveOperator(), ct);
        return result.Success
            ? Ok(new { code = 200, message = result.Message, data = result.Rule is null ? null : ToRuleDto(result.Rule) })
            : BadRequest(new { code = 400, message = result.Message, data = result.Rule is null ? null : ToRuleDto(result.Rule) });
    }

    /// <summary>Deletes a rule; any incident it left open is closed silently.</summary>
    [HttpDelete("rules/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var result = await _alerts.DeleteRuleAsync(id, ResolveOperator(), ct);
        return result.Success
            ? Ok(new { code = 200, message = result.Message })
            : NotFound(new { code = 404, message = result.Message });
    }

    /// <summary>
    /// Sends a synthetic notification so the operator can prove the channel is wired up.
    /// </summary>
    /// <remarks>
    /// This exists because "configured" and "delivering" are different states, and the usual way to
    /// discover the difference is a real outage that nobody hears about.
    /// </remarks>
    [HttpPost("rules/{id}/test")]
    public async Task<IActionResult> Test(string id, CancellationToken ct)
    {
        var result = await _alerts.TestAsync(id, ResolveOperator(), ct);
        return result.Success
            ? Ok(new { code = 200, message = result.Message })
            : NotFound(new { code = 404, message = result.Message });
    }

    /// <summary>Silences one incident for a while without disarming the rule.</summary>
    [HttpPost("incidents/{id}/mute")]
    public async Task<IActionResult> Mute(string id, [FromBody] MuteRequest? body, CancellationToken ct)
    {
        var result = await _alerts.MuteAsync(id, body?.Minutes ?? 60, ResolveOperator(), ct);
        return result.Success
            ? Ok(new { code = 200, message = result.Message, data = result.Incident is null ? null : ToIncidentDto(result.Incident, _time.GetUtcNow()) })
            : NotFound(new { code = 404, message = result.Message });
    }

    private string ResolveOperator()
    {
        try
        {
            var name = User?.Identity?.Name;
            return string.IsNullOrWhiteSpace(name) ? "dashboard-user" : name!;
        }
        catch
        {
            return "dashboard-user";
        }
    }

    private object ToRuleDto(MetricAlertRule rule) => new
    {
        id = rule.Id,
        name = rule.Name,
        kind = rule.Kind.ToString(),
        scopeKind = rule.ScopeKind.ToString(),
        scope = rule.Scope,
        threshold = rule.Threshold,
        thresholdLabel = MetricAlertEvaluator.Format(rule.Kind, rule.Threshold),
        windowSeconds = rule.WindowSeconds,
        forSeconds = rule.ForSeconds,
        minSamples = rule.MinSamples,
        severity = rule.Severity.ToString(),
        enabled = rule.Enabled,
        notifyOnResolve = rule.NotifyOnResolve,
        renotifyMinutes = rule.RenotifyMinutes,
        createdAt = rule.CreatedAt,
        createdBy = rule.CreatedBy
    };

    private object ToIncidentDto(MetricAlertIncident incident, DateTimeOffset now) => new
    {
        id = incident.Id,
        ruleId = incident.RuleId,
        ruleName = incident.RuleName,
        kind = incident.Kind.ToString(),
        scopeKind = incident.ScopeKind.ToString(),
        scope = incident.Scope,
        severity = incident.Severity.ToString(),
        state = incident.State.ToString(),
        threshold = incident.Threshold,
        thresholdLabel = MetricAlertEvaluator.Format(incident.Kind, incident.Threshold),
        lastValue = incident.LastValue,
        lastValueLabel = MetricAlertEvaluator.Format(incident.Kind, incident.LastValue),
        peakValueLabel = MetricAlertEvaluator.Format(incident.Kind, incident.PeakValue),
        lastSamples = incident.LastSamples,
        observedSeconds = incident.ObservedSeconds,
        startedAt = incident.StartedAt,
        firingAt = incident.FiringAt,
        resolvedAt = incident.ResolvedAt,
        elapsedSeconds = (long)incident.Elapsed(now).TotalSeconds,
        mutedUntil = incident.MutedUntil,
        isMuted = incident.IsMutedAt(now),
        lastNotifiedAt = incident.LastNotifiedAt,
        notifications = incident.Notifications,
        resolveReason = incident.ResolveReason,
        lastCode = incident.LastCode,
        lastReason = incident.LastReason,
        isActive = incident.IsActive
    };

    /// <summary>
    /// Metric catalogue. The unit travels with the metric so the editor can label the threshold
    /// input correctly instead of letting someone type <c>5</c> into a field that means 500%.
    /// </summary>
    private object[] KindCatalog() =>
    [
        new
        {
            kind = nameof(MetricAlertKind.ErrorRate),
            unit = "ratio",
            defaultThreshold = _options.DefaultErrorRateThreshold,
            supportsRouteScope = true
        },
        new
        {
            kind = nameof(MetricAlertKind.LatencyP99),
            unit = "seconds",
            defaultThreshold = _options.DefaultLatencyP99Seconds,
            supportsRouteScope = true
        },
        new
        {
            kind = nameof(MetricAlertKind.CircuitOpen),
            unit = "count",
            defaultThreshold = 1.0,
            supportsRouteScope = false
        }
    ];

    private object ScopeCatalog()
    {
        var clusters = _config.GetClusters()
            .Select(c => c.ClusterId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var routes = _config.GetRoutes()
            .Select(r => r.RouteId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new { clusters, routes };
    }
}

/// <summary>How long to silence an incident.</summary>
public sealed class MuteRequest
{
    /// <summary>Minutes; clamped server-side to 1..10080.</summary>
    public int Minutes { get; set; } = 60;
}
