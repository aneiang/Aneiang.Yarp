using Aneiang.Yarp.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Controllers;

/// <summary>
/// Read-only view of the dynamic configuration's identity.
///
/// The dashboard uses <c>version</c> for optimistic concurrency: it is captured when a
/// form opens and echoed back on save, so a second editor cannot silently overwrite the
/// first one's change. It is also the cheapest way for an operator (or a script) to ask
/// "has anything changed since I looked last?" without diffing the whole config.
/// </summary>
[Route("api/config")]
[ApiController]
public class ConfigStateController : ControllerBase
{
    private readonly IDynamicYarpConfigService _config;

    public ConfigStateController(IDynamicYarpConfigService config)
    {
        _config = config;
    }

    /// <summary>Current config version, shape summary and last-modified timestamp.</summary>
    [HttpGet("state")]
    public IActionResult GetState()
    {
        var config = _config.GetDynamicConfig();

        return Ok(new
        {
            code = 200,
            data = new
            {
                version = config?.Version ?? 0,
                lastModified = config?.LastModified,
                routes = _config.GetRoutes().Count,
                clusters = _config.GetClusters().Count
            }
        });
    }
}
