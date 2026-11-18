using System.Text.Json;
using Aneiang.Yarp.Dashboard.Infrastructure.Policies;
using Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;
using Aneiang.Yarp.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Controllers;

/// <summary>
/// Pre-flight diagnostics for configuration: validate-before-save, whole-config integrity
/// check, and the list of policy names a route may reference.
///
/// All three are read-only. Nothing here writes to the live configuration, which is the
/// whole point — the save endpoints can be called after a green result, or the operator
/// can just look.
/// </summary>
[Route("api/config")]
[ApiController]
public class ConfigDiagnosticsController : ControllerBase
{
    private readonly IConfigurationValidator _validator;
    private readonly IGatewayPolicyCatalog _policies;

    public ConfigDiagnosticsController(IConfigurationValidator validator, IGatewayPolicyCatalog policies)
    {
        _validator = validator;
        _policies = policies;
    }

    /// <summary>
    /// Validates one route or cluster without saving it.
    /// Body: <c>{ "kind": "route"|"cluster", "config": {...}, "clusterWillBeCreated": false }</c>
    /// </summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            return BadRequest(new { code = 400, message = "Body must be an object" });

        var kind = body.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
        if (!body.TryGetProperty("config", out var configElement) || configElement.ValueKind != JsonValueKind.Object)
            return BadRequest(new { code = 400, message = "config is required" });

        var createCluster = body.TryGetProperty("clusterWillBeCreated", out var createProp)
                            && createProp.ValueKind == JsonValueKind.True;

        try
        {
            if (string.Equals(kind, "route", StringComparison.OrdinalIgnoreCase))
            {
                var route = YarpJsonConfig.DeserializeRoute(configElement);
                if (route == null)
                    return BadRequest(new { code = 400, message = "Route configuration is required" });

                var report = await _validator.ValidateRouteAsync(route, createCluster);
                return Ok(new { code = 200, data = report });
            }

            if (string.Equals(kind, "cluster", StringComparison.OrdinalIgnoreCase))
            {
                var cluster = YarpJsonConfig.DeserializeCluster(configElement);
                if (cluster == null)
                    return BadRequest(new { code = 400, message = "Cluster configuration is required" });

                var report = await _validator.ValidateClusterAsync(cluster);
                return Ok(new { code = 200, data = report });
            }

            return BadRequest(new { code = 400, message = "kind must be 'route' or 'cluster'" });
        }
        catch (JsonException ex)
        {
            // A parse failure is a finding, not a transport error: report it the same way
            // so the form can highlight the offending property instead of a raw 400.
            var field = string.IsNullOrWhiteSpace(ex.Path) ? "config" : ex.Path!.TrimStart('$', '.');
            var report = ConfigValidationReport.From(new[]
            {
                new ConfigValidationIssue(nameof(ValidationSeverity.Error), field, ex.Message)
            });
            return Ok(new { code = 200, data = report });
        }
    }

    /// <summary>Cross-checks the entire live configuration.</summary>
    [HttpGet("integrity")]
    public async Task<IActionResult> Integrity()
    {
        var report = await _validator.ValidateAllAsync();
        return Ok(new { code = 200, data = report });
    }

    /// <summary>
    /// Every policy name that may be referenced by a route, grouped by kind.
    /// <c>meta.unverifiable</c> lists the kinds ASP.NET Core cannot enumerate — a miss there
    /// is only a warning, never a rejection.
    /// </summary>
    [HttpGet("policies")]
    public IActionResult GetPolicies()
    {
        List<object> Map(GatewayPolicyKind kind) => _policies.GetPolicies(kind)
            .Select(p => (object)new { name = p.Name, description = p.Description, builtIn = p.BuiltIn })
            .ToList();

        return Ok(new
        {
            code = 200,
            data = new
            {
                authorization = Map(GatewayPolicyKind.Authorization),
                cors = Map(GatewayPolicyKind.Cors),
                rateLimiter = Map(GatewayPolicyKind.RateLimiter),
                outputCache = Map(GatewayPolicyKind.OutputCache)
            },
            meta = new
            {
                unverifiable = new[] { "rateLimiter", "outputCache" }
            }
        });
    }
}
