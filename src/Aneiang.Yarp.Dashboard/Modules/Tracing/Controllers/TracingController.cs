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
    private readonly TracingAuthStore _auth;
    private readonly TracingExportProbe _probe;

    /// <summary>Initializes a new instance of the <see cref="TracingController"/> class.</summary>
    public TracingController(IGatewayTracingService tracing, TracingAuthStore auth, TracingExportProbe probe)
    {
        _tracing = tracing;
        _auth = auth;
        _probe = probe;
    }

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

    /// <summary>
    /// Replaces the export authentication headers the dashboard writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the panel-owned override file is touched; <c>Gateway:Dashboard:Tracing:Headers</c> and
    /// the standard <c>OTEL_EXPORTER_OTLP_HEADERS</c> variable are left alone and keep working as
    /// fallbacks. A row with an empty value means "no panel override", not "blank credential", so
    /// the page can be saved without re-typing every token it is not allowed to display.
    /// </para>
    /// <para>
    /// The response never contains a credential: it returns the same masked status the GET does.
    /// </para>
    /// </remarks>
    /// <param name="request">Headers to write. Empty list clears the overrides.</param>
    [HttpPost("auth")]
    public IActionResult SetAuth([FromBody] TracingAuthRequest request)
    {
        var pairs = (request?.Headers ?? [])
            .Select(h => new KeyValuePair<string, string>(h.Name ?? string.Empty, h.Value ?? string.Empty))
            .ToList();

        if (!_auth.TrySave(pairs, out var error))
            return BadRequest(new { code = 400, message = error });

        return Ok(new
        {
            code = 200,
            message = "export headers saved",
            // The tracer provider is built once at startup, so a saved credential is not in effect
            // until the process restarts. Saying so here is more useful than letting the page show
            // the new value while the exporter still sends the old one.
            restartRequired = true,
            data = _tracing.GetStatus()
        });
    }

    /// <summary>
    /// Sends one empty OTLP batch to the configured endpoint and reports the outcome.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trace list on this page comes from the tracer provider's processor, so it stays populated
    /// while the exporter is failing — the two are separate branches of the same provider. Export
    /// failures are asynchronous and silent, which makes "the page has data" a misleading signal.
    /// This endpoint is the only on-demand way to tell whether the export path works.
    /// </para>
    /// <para>
    /// Returns 200 whenever the probe ran, including when the endpoint refused the batch: the HTTP
    /// call succeeded, and the verdict is in <c>data.ok</c>. A non-200 here would mean the probe
    /// itself broke, which is a different problem for the caller.
    /// </para>
    /// <para>
    /// Admin-only, like the credential endpoint. It exercises the export credential and makes the
    /// gateway issue a request on the operator's behalf, so it sits in the same role tier.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancelled when the caller disconnects.</param>
    [HttpPost("verify")]
    public async Task<IActionResult> Verify(CancellationToken cancellationToken)
    {
        var result = await _probe.RunAsync(cancellationToken);
        return Ok(new { code = 200, data = result });
    }
}

/// <summary>Body of <c>POST /api/tracing/auth</c>.</summary>
public sealed class TracingAuthRequest
{
    /// <summary>Headers to write. An empty or null list clears the panel overrides.</summary>
    public List<TracingAuthHeaderInput>? Headers { get; set; }
}

/// <summary>One header row submitted from the tracing page.</summary>
public sealed class TracingAuthHeaderInput
{
    /// <summary>Header name, e.g. <c>Authorization</c>.</summary>
    public string? Name { get; set; }

    /// <summary>Header value. Empty keeps the current panel override withdrawn.</summary>
    public string? Value { get; set; }
}

/// <summary>Body of <c>POST /api/tracing/sampling</c>.</summary>
public sealed class SamplingRequest
{
    /// <summary>Fraction of traces to record, 0 to 1.</summary>
    public double Ratio { get; set; }
}
