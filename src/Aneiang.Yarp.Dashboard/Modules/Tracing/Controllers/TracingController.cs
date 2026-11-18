using Aneiang.Yarp.Dashboard.Modules.Tracing.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aneiang.Yarp.Dashboard.Modules.Tracing.Controllers;

/// <summary>
/// Live tracing state and the recent-trace buffer.
/// </summary>
/// <remarks>
/// The traces returned here come from the tracer provider's own processor, so they are exactly the
/// traces that were sampled - not a parallel copy. That matters when the question is "why is my
/// trace missing from Tempo": if it is not here either, it was never sampled, and the sampler
/// counters on the same response say so.
/// </remarks>
[Route("api/tracing")]
[ApiController]
public sealed class TracingController : ControllerBase
{
    private readonly IGatewayTracingService _tracing;

    /// <summary>Initializes a new instance of the <see cref="TracingController"/> class.</summary>
    public TracingController(IGatewayTracingService tracing) => _tracing = tracing;

    /// <summary>Status plus the most recent traces.</summary>
    /// <param name="limit">How many traces to return; clamped to 200.</param>
    [HttpGet]
    public IActionResult List([FromQuery] int limit = 50)
        => Ok(new
        {
            code = 200,
            data = new
            {
                status = _tracing.GetStatus(),
                traces = _tracing.GetTraces(limit)
            }
        });

    /// <summary>One trace with its span waterfall.</summary>
    [HttpGet("traces/{traceId}")]
    public IActionResult Trace(string traceId)
    {
        var trace = _tracing.GetTrace(traceId);
        return trace is null
            ? NotFound(new { code = 404, message = $"Trace '{traceId}' is not in the buffer. It may have been evicted or never sampled." })
            : Ok(new { code = 200, data = trace });
    }

    /// <summary>Changes the sampling ratio applied to new traces.</summary>
    /// <param name="request">The new ratio, 0 to 1.</param>
    [HttpPost("sampling")]
    public IActionResult SetSampling([FromBody] SamplingRequest request)
    {
        if (!_tracing.TrySetSamplingRatio(request.Ratio, out var error))
            return BadRequest(new { code = 400, message = error });

        return Ok(new
        {
            code = 200,
            message = "sampling ratio updated",
            data = _tracing.GetStatus()
        });
    }

    /// <summary>Empties the in-memory trace buffer.</summary>
    [HttpPost("clear")]
    public IActionResult Clear()
    {
        _tracing.ClearTraces();
        return Ok(new { code = 200, message = "trace buffer cleared" });
    }
}

/// <summary>Body of <c>POST /api/tracing/sampling</c>.</summary>
public sealed class SamplingRequest
{
    /// <summary>Fraction of traces to record, 0 to 1.</summary>
    public double Ratio { get; set; }
}
