using Aneiang.Yarp.Dashboard.Modules.Environments;
using Aneiang.Yarp.GatewayEnvironments;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Aneiang.Yarp.Dashboard.Modules.Dashboard.Controllers;

/// <summary>
/// Exposes the environment the gateway serves and lets the operator point the control plane at
/// another one for editing.
/// </summary>
/// <remarks>
/// Two different things are on offer here and the UI must not blur them:
/// the <b>runtime</b> environment is what is being forwarded and cannot be changed at all;
/// the <b>editing view</b> is what the pages read and write and can be switched freely.
/// </remarks>
[ApiController]
[Route("api/environments")]
public sealed partial class EnvironmentsController : ControllerBase
{
    private readonly GatewayEnvironmentRegistry _registry;
    private readonly IDynamicYarpConfigService _dynamicConfig;
    private readonly EnvironmentPromotionService _promotion;
    private readonly ILogger<EnvironmentsController> _logger;

    public EnvironmentsController(
        GatewayEnvironmentRegistry registry,
        IDynamicYarpConfigService dynamicConfig,
        EnvironmentPromotionService promotion,
        ILogger<EnvironmentsController> logger)
    {
        _registry = registry;
        _dynamicConfig = dynamicConfig;
        _promotion = promotion;
        _logger = logger;
    }

    /// <summary>Current editing view, the runtime environment, and the full list.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        var current = _registry.CurrentEnvironment;

        return Ok(new
        {
            code = 200,
            data = new
            {
                runtime = _registry.RuntimeEnvironment,
                current,
                isEditingRuntime = _registry.IsEditingRuntime,
                multiEnvironment = _registry.Environments.Count > 1,
                environments = _registry.Environments.Select(e => new
                {
                    name = e.Name,
                    isRuntime = e.IsRuntime,
                    isCurrent = string.Equals(e.Name, current, StringComparison.OrdinalIgnoreCase)
                })
            }
        });
    }

    /// <summary>
    /// Switch the editing view. Reloads the working set from the target environment's store and
    /// publishes it only when the target is the runtime environment.
    /// </summary>
    [HttpPost("switch")]
    public async Task<IActionResult> Switch([FromBody] SwitchEnvironmentRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Environment))
            return BadRequest(new { code = 400, message = "environment is required" });

        try
        {
            var result = await _dynamicConfig.SwitchEnvironmentAsync(request.Environment, ct);

            return Ok(new
            {
                code = 200,
                data = new
                {
                    previous = result.PreviousEnvironment,
                    current = result.CurrentEnvironment,
                    runtime = result.RuntimeEnvironment,
                    published = result.Published,
                    routeCount = result.RouteCount,
                    clusterCount = result.ClusterCount
                }
            });
        }
        catch (ArgumentException ex)
        {
            // Unknown / malformed name. The message already lists what is available.
            return BadRequest(new { code = 400, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch environment to {Environment}", request.Environment);
            return StatusCode(500, new
            {
                code = 500,
                message = "The environment store could not be read. The editing view was left unchanged."
            });
        }
    }
}

/// <summary>Request body for <c>POST /api/environments/switch</c>.</summary>
public sealed class SwitchEnvironmentRequest
{
    /// <summary>Name of the environment to edit. Must be one of the configured environments.</summary>
    public string? Environment { get; set; }
}

/// <summary>
/// Comparing and promoting between environments.
/// </summary>
/// <remarks>
/// Both operations read and write stores directly instead of switching the editing view first:
/// switching reloads the working set and publishes when the target is the runtime environment, so
/// merely rendering a diff would deploy the configuration it was describing.
/// </remarks>
public sealed partial class EnvironmentsController
{
    /// <summary>
    /// The change a promotion would make, source → target. Read-only, no token, safe to call as often as the UI likes.
    /// </summary>
    [HttpGet("diff")]
    public async Task<IActionResult> Diff([FromQuery] string? source, [FromQuery] string? target, CancellationToken ct)
    {
        if (_registry.Environments.Count < 2)
            return BadRequest(new { code = 400, message = "Only one environment is configured; there is nothing to compare." });

        try
        {
            var diff = await _promotion.DiffAsync(source, target, ct);
            return Ok(new { code = 200, data = diff });
        }
        catch (PromotionException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.StatusCode, message = ex.Message });
        }
        catch (IOException ex)
        {
            return StatusCode(500, new { code = 500, message = ex.Message });
        }
    }

    /// <summary>
    /// Validates the change and returns a single-use token. Nothing is written.
    /// </summary>
    [HttpPost("promote/prepare")]
    public async Task<IActionResult> PreparePromotion([FromBody] PreparePromotionRequest request, CancellationToken ct)
    {
        if (_registry.Environments.Count < 2)
            return BadRequest(new { code = 400, message = "Only one environment is configured; there is nothing to promote." });

        try
        {
            var prepared = await _promotion.PrepareAsync(request ?? new PreparePromotionRequest(), ct);
            return Ok(new { code = 200, data = prepared });
        }
        catch (PromotionException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.StatusCode, message = ex.Message });
        }
        catch (IOException ex)
        {
            return StatusCode(500, new { code = 500, message = ex.Message });
        }
    }

    /// <summary>
    /// Applies a prepared promotion. Writes the target store and, when the target is the runtime
    /// environment, publishes the result.
    /// </summary>
    [HttpPost("promote/apply")]
    public async Task<IActionResult> ApplyPromotion([FromBody] ApplyPromotionRequest request, CancellationToken ct)
    {
        try
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            var result = await _promotion.ApplyAsync(request?.Token ?? string.Empty, clientIp, ct);

            return Ok(new
            {
                code = 200,
                data = new
                {
                    source = result.Source,
                    target = result.Target,
                    routes = result.Routes,
                    clusters = result.Clusters,
                    published = result.Published
                }
            });
        }
        catch (PromotionException ex)
        {
            return StatusCode(ex.StatusCode, new { code = ex.StatusCode, message = ex.Message });
        }
        catch (IOException ex)
        {
            return StatusCode(500, new { code = 500, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Environment promotion failed");
            return StatusCode(500, new { code = 500, message = "Promotion failed: " + ex.Message });
        }
    }
}
