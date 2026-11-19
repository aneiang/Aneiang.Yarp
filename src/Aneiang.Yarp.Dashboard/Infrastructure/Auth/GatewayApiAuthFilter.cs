using System.Text;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Dashboard.Infrastructure.Auth;

/// <summary>Authorization filter for GatewayConfigController. Supports BasicAuth and ApiKey modes.</summary>
internal sealed class GatewayApiAuthFilter : IAsyncAuthorizationFilter
{
    private readonly GatewayApiAuthOptions _opts;
    public GatewayApiAuthFilter(IOptions<GatewayApiAuthOptions> options) => _opts = options.Value;

    public Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var req = ctx.HttpContext.Request;
        var ok = _opts.Mode switch
        {
            GatewayApiAuthMode.None => true,
            GatewayApiAuthMode.BasicAuth => ValidateBasic(req),
            GatewayApiAuthMode.ApiKey => ValidateApiKey(req),
            _ => false
        };

        if (!ok)
            ctx.Result = new JsonResult(new { code = 401, message = "Unauthorized" }) { StatusCode = 401 };

        return Task.CompletedTask;
    }

    private bool ValidateBasic(HttpRequest req)
    {
        if (string.IsNullOrWhiteSpace(_opts.Username) || string.IsNullOrWhiteSpace(_opts.Password))
            return false;

        var h = req.Headers["Authorization"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(h) || !h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(h[6..]));
            var idx = decoded.IndexOf(':');
            // Constant-time on both halves: == returns at the first difference, which is a
            // measurable oracle for a credential that is checked on every gateway API call.
            return idx >= 0
                && SecureCompare.Equals(decoded[..idx], _opts.Username)
                && SecureCompare.Equals(decoded[(idx + 1)..], _opts.Password);
        }
        catch (FormatException)
        {
            // Invalid Base64 encoding
            return false;
        }
    }

    private bool ValidateApiKey(HttpRequest req)
    {
        if (string.IsNullOrWhiteSpace(_opts.ApiKey)) return false;

        if (req.Headers.TryGetValue(_opts.ApiKeyHeaderName, out var hv)
            && hv.Any(v => SecureCompare.Equals(v, _opts.ApiKey)))
            return true;

        return _opts.AllowApiKeyInQuery
            && req.Query.TryGetValue("api-key", out var qv)
            && qv.Any(v => SecureCompare.Equals(v, _opts.ApiKey));
    }
}
