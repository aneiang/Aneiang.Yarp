using System.Text.Json;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Controllers;

/// <summary>Gateway config API: dynamic route registration, deletion, and query.</summary>
[Route("api/gateway")]
[ApiController]
[Produces("application/json")]
public class GatewayConfigController : ControllerBase
{
    private readonly IDynamicYarpConfigService _dynamicConfig;
    private readonly IConfigurationGuard _guard;
    private readonly ILogger<GatewayConfigController> _logger;

    /// <summary>Creates a new instance of the controller.</summary>
    public GatewayConfigController(
        IDynamicYarpConfigService dynamicConfig,
        IConfigurationGuard guard,
        ILogger<GatewayConfigController> logger)
    {
        _dynamicConfig = dynamicConfig;
        _guard = guard;
        _logger = logger;
    }

    /// <summary>
    /// Optimistic-concurrency gate shared by every write endpoint in this controller.
    ///
    /// The self-registration API is the highest-risk writer in the product — it is called by
    /// unattended services, so a bad payload reaches production configuration without anyone
    /// looking at it. The version echo is optional (older clients simply omit it), but when a
    /// caller does send <c>X-Config-Version</c> a stale value is refused with 409 instead of
    /// silently overwriting a concurrent edit.
    /// </summary>
    private IActionResult? RejectStaleWrite()
    {
        var expected = ConfigConcurrency.ReadExpectedVersion(Request);
        var current = _dynamicConfig.GetDynamicConfig()?.Version ?? 0;
        if (!ConfigConcurrency.IsStale(expected, current))
            return null;

        return Conflict(ConfigConcurrency.BuildConflictPayload(expected!.Value, current));
    }

    /// <summary>
    /// Projects a registration request onto the route shape YARP will actually build, so the
    /// guard checks the same object that ends up in the configuration.
    /// </summary>
    private static RouteConfig ToRouteConfig(RegisterRouteRequest request) => new()
    {
        RouteId = request.RouteName,
        ClusterId = request.ClusterName,
        Match = new RouteMatch { Path = request.MatchPath },
        Order = request.Order ?? int.MaxValue,
        Transforms = request.Transforms?.Select(t => (IReadOnlyDictionary<string, string>)t).ToList()
    };

    private static ClusterConfig ToClusterConfig(CreateClusterRequest request) => new()
    {
        ClusterId = request.ClusterId,
        Destinations = request.Destinations.ToDictionary(
            kvp => kvp.Key,
            kvp => new DestinationConfig { Address = kvp.Value }),
        LoadBalancingPolicy = request.LoadBalancingPolicy
    };

    /// <summary>
    /// Applies an update patch on top of the cluster currently in effect, so the guard sees the
    /// post-write configuration rather than the fragment the caller sent.
    /// </summary>
    private ClusterConfig BuildMergedCluster(string clusterId, UpdateClusterRequest patch)
    {
        var existing = _dynamicConfig.GetCluster(clusterId);

        var destinations = patch.Destinations is { Count: > 0 }
            ? patch.Destinations.ToDictionary(kvp => kvp.Key, kvp => new DestinationConfig { Address = kvp.Value })
            : existing?.Destinations;

        return new ClusterConfig
        {
            ClusterId = clusterId,
            Destinations = destinations,
            LoadBalancingPolicy = patch.LoadBalancingPolicy ?? existing?.LoadBalancingPolicy,
            HealthCheck = existing?.HealthCheck,
            SessionAffinity = existing?.SessionAffinity,
            HttpClient = existing?.HttpClient,
            HttpRequest = existing?.HttpRequest
        };
    }

    /// <summary>Register or update a route and its cluster.</summary>
    [HttpPost("register-route")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterRoute([FromBody] RegisterRouteRequest request)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            return BadRequest(new { code = 400, message = string.Join("; ", errors) });
        }

        if (RejectStaleWrite() is { } stale)
            return stale;

        // The cluster is created alongside the route when it is missing, so an unknown
        // ClusterName is not an error here — but everything else about the route is checked.
        var clusterWillBeCreated = _dynamicConfig.GetCluster(request.ClusterName) == null;
        var guard = await _guard.CheckRouteAsync(ToRouteConfig(request), clusterWillBeCreated, HttpContext.RequestAborted);
        if (!guard.Ok)
            return BadRequest(guard.ToErrorPayload());

        var result = await _dynamicConfig.TryAddRoute(request);
        return Ok(new { code = 200, message = result.Message });
    }

    /// <summary>Delete a route. Also removes the cluster if no remaining routes reference it.</summary>
    /// <param name="routeName">Route name to delete.</param>
    /// <param name="clientIp">Optional client IP for IP-based isolation: only removes the matching destination.</param>
    [HttpDelete("{routeName}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRoute(string routeName, [FromQuery] string? clientIp = null)
    {
        var result = await _dynamicConfig.TryRemoveRoute(routeName, clientIp);
        return result.Success
            ? Ok(new { code = 200, message = result.Message })
            : NotFound(new { code = 404, message = result.Message });
    }

    /// <summary>Get all registered routes.</summary>
    [HttpGet("routes")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult GetRoutes()
    {
        var data = _dynamicConfig.GetRoutes().Select(r => new
        {
            routeId = r.RouteId,
            clusterId = r.ClusterId,
            path = r.Match.Path,
            order = r.Order
        });
        return Ok(new { code = 200, data });
    }

    /// <summary>Get dynamic configuration (routes and clusters with metadata).</summary>
    [HttpGet("dynamic-config")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult GetDynamicConfig()
    {
        var config = _dynamicConfig.GetDynamicConfig();
        return Ok(new { code = 200, data = config });
    }

    /// <summary>Update a route's configuration (JSON format).</summary>
    [HttpPut("routes/{routeId}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRoute(string routeId, [FromBody] JsonElement config)
    {
        try
        {
            // Validate required fields
            if (!config.TryGetProperty("clusterId", out var clusterIdProp) ||
                !config.TryGetProperty("matchPath", out var matchPathProp))
            {
                return BadRequest(new { code = 400, message = "clusterId and matchPath are required" });
            }

            if (RejectStaleWrite() is { } stale)
                return stale;

            var request = new RegisterRouteRequest
            {
                RouteName = routeId,
                ClusterName = clusterIdProp.GetString() ?? string.Empty,
                MatchPath = matchPathProp.GetString() ?? string.Empty,
                Order = config.TryGetProperty("order", out var orderProp) ? orderProp.GetInt32() : int.MaxValue,
                Transforms = config.TryGetProperty("transforms", out var transformsProp)
                    ? transformsProp.Deserialize<List<Dictionary<string, string>>>()
                    : null
            };

            var clusterWillBeCreated = _dynamicConfig.GetCluster(request.ClusterName) == null;
            var guard = await _guard.CheckRouteAsync(
                ToRouteConfig(request), clusterWillBeCreated, HttpContext.RequestAborted);
            if (!guard.Ok)
                return BadRequest(guard.ToErrorPayload());

            var result = await _dynamicConfig.TryAddRoute(request);
            return result.Success
                ? Ok(new { code = 200, message = result.Message })
                : BadRequest(new { code = 400, message = result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update route {RouteId}: invalid JSON", routeId);
            return BadRequest(new { code = 400, message = $"Invalid JSON: {ex.Message}" });
        }
    }

    /// <summary>Delete a cluster (if no routes reference it).</summary>
    [HttpDelete("clusters/{clusterId}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DeleteCluster(string clusterId)
    {
        var result = await _dynamicConfig.TryRemoveCluster(clusterId);
        return result.Success
            ? Ok(new { code = 200, message = result.Message })
            : BadRequest(new { code = 400, message = result.Message });
    }

    /// <summary>Create a new cluster.</summary>
    [HttpPost("clusters")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateCluster([FromBody] CreateClusterRequest request)
    {
        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            return BadRequest(new { code = 400, message = string.Join("; ", errors) });
        }

        if (RejectStaleWrite() is { } stale)
            return stale;

        var guard = await _guard.CheckClusterAsync(ToClusterConfig(request), HttpContext.RequestAborted);
        if (!guard.Ok)
            return BadRequest(guard.ToErrorPayload());

        var result = await _dynamicConfig.TryAddCluster(request);
        return result.Success
            ? Ok(new { code = 200, message = result.Message })
            : BadRequest(new { code = 400, message = result.Message });
    }

    /// <summary>Update an existing cluster.</summary>
    [HttpPut("clusters/{clusterId}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCluster(string clusterId, [FromBody] UpdateClusterRequest request)
    {
        if (RejectStaleWrite() is { } stale)
            return stale;

        // Validate the merged result, not the patch: a partial payload is legitimate, but the
        // configuration it produces is what has to be legal.
        var guard = await _guard.CheckClusterAsync(BuildMergedCluster(clusterId, request), HttpContext.RequestAborted);
        if (!guard.Ok)
            return BadRequest(guard.ToErrorPayload());

        var result = await _dynamicConfig.TryUpdateCluster(clusterId, request);
        return result.Success
            ? Ok(new { code = 200, message = result.Message })
            : (result.Message.Contains("not found")
                ? NotFound(new { code = 404, message = result.Message })
                : BadRequest(new { code = 400, message = result.Message }));
    }

    /// <summary>Health check endpoint.</summary>
    [HttpGet("ping")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult Ping() => Ok(new { code = 200, message = "pong" });

    /// <summary>
    /// Heartbeat endpoint for registered services.
    /// Services call this periodically to keep their registration alive.
    /// Gateway tracks last heartbeat time to detect stale registrations.
    /// </summary>
    /// <param name="routeName">Route name to update heartbeat for.</param>
    /// <param name="clientIp">Optional client IP for IP-based isolation.</param>
    [HttpPost("{routeName}/heartbeat")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public IActionResult Heartbeat(string routeName, [FromQuery] string? clientIp = null)
    {
        var updated = _dynamicConfig.UpdateHeartbeat(routeName, clientIp);
        if (!updated)
            return NotFound(new { code = 404, message = $"Route '{routeName}' not found" });

        return Ok(new { code = 200, message = "heartbeat" });
    }

    #region Batch Operations

    /// <summary>Batch register routes and clusters in a single atomic operation.</summary>
    [HttpPost("batch/register")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BatchRegister([FromBody] BatchRegisterRequest request)
    {
        if (request.Routes.Count == 0)
            return BadRequest(new { code = 400, message = "At least one route is required" });

        if (RejectStaleWrite() is { } stale)
            return stale;

        var results = new List<object>();
        var allSucceeded = true;

        foreach (var routeReq in request.Routes)
        {
            try
            {
                // Per-item guard rather than all-or-nothing: the batch response already reports
                // success per route, and one bad entry must not discard the other 49.
                var clusterWillBeCreated = _dynamicConfig.GetCluster(routeReq.ClusterName) == null;
                var guard = await _guard.CheckRouteAsync(
                    ToRouteConfig(routeReq), clusterWillBeCreated, HttpContext.RequestAborted);

                if (!guard.Ok)
                {
                    results.Add(new
                    {
                        route = routeReq.RouteName,
                        success = false,
                        message = $"{guard.Errors[0].Field}: {guard.Errors[0].Message}",
                        validation = new { errors = guard.Errors, warnings = guard.Warnings }
                    });
                    allSucceeded = false;
                    continue;
                }

                var result = await _dynamicConfig.TryAddRoute(routeReq, request.Source ?? "batch", request.CreatedBy);
                results.Add(new { route = routeReq.RouteName, success = result.Success, message = result.Message });
                if (!result.Success)
                    allSucceeded = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch register failed for route {RouteName}", routeReq.RouteName);
                results.Add(new { route = routeReq.RouteName, success = false, message = SafeErrorMessages.Create(HttpContext, "Batch route registration failed", ex) });
                allSucceeded = false;
            }
        }

        var summary = allSucceeded ? "All operations succeeded" : "Some operations failed";
        return Ok(new { code = 200, message = summary, details = results });
    }

    /// <summary>Batch delete routes in a single atomic operation.</summary>
    [HttpPost("batch/delete-routes")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> BatchDeleteRoutes([FromBody] BatchDeleteRoutesRequest request)
    {
        if (request.RouteNames.Count == 0)
            return BadRequest(new { code = 400, message = "At least one route name is required" });

        var results = new List<object>();
        var allSucceeded = true;

        foreach (var routeName in request.RouteNames)
        {
            try
            {
                var result = await _dynamicConfig.TryRemoveRoute(routeName, request.ClientIp, request.RemoveOrphanedClusters);
                results.Add(new { route = routeName, success = result.Success, message = result.Message });
                if (!result.Success)
                    allSucceeded = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Batch delete failed for route {RouteName}", routeName);
                results.Add(new { route = routeName, success = false, message = ex.Message });
                allSucceeded = false;
            }
        }

        var summary = allSucceeded ? "All operations succeeded" : "Some operations failed";
        return Ok(new { code = 200, message = summary, details = results });
    }

    #endregion
}

/// <summary>Request model for batch register operation.</summary>
public class BatchRegisterRequest
{
    /// <summary>
    /// Gets or sets the routes.
    /// </summary>
    public List<RegisterRouteRequest> Routes { get; set; } = new();
    /// <summary>
    /// Gets or sets the source.
    /// </summary>
    public string? Source { get; set; }
    /// <summary>
    /// Gets or sets the created by.
    /// </summary>
    public string? CreatedBy { get; set; }
}

/// <summary>Request model for batch delete routes operation.</summary>
public class BatchDeleteRoutesRequest
{
    /// <summary>
    /// Gets or sets the route names.
    /// </summary>
    public List<string> RouteNames { get; set; } = new();
    /// <summary>
    /// Gets or sets the client ip.
    /// </summary>
    public string? ClientIp { get; set; }
    /// <summary>
    /// Gets or sets a value indicating whether remove orphaned clusters.
    /// </summary>
    public bool RemoveOrphanedClusters { get; set; } = true;
}
