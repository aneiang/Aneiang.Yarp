using System.Text.Json;
using Aneiang.Yarp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Model;

namespace AneiangYarpPlugin;

/// <summary>
/// Stamps the configured header on every proxied response.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the configuration comes from.</b> The gateway compiles every live binding into an
/// immutable snapshot (<see cref="GatewaySnapshot"/>). Reading
/// <see cref="IGatewaySnapshotPublisher.Current"/> once per request means a configuration change is
/// visible on the next request - no restart, no reload, and no cache to invalidate. Capturing the
/// configuration in the constructor would freeze it until the plugin is reloaded.
/// </para>
/// <para>
/// A binding only appears in the snapshot when both the plugin and the binding are enabled, so
/// finding one is enough to know it is live. Unreadable JSON must not break forwarding: log it and
/// fall back to the defaults.
/// </para>
/// </remarks>
public sealed class AneiangYarpPluginMiddleware
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<AneiangYarpPluginMiddleware> _logger;
    private readonly IGatewaySnapshotPublisher _snapshots;

    /// <summary>Constructor invoked by the host's DI container.</summary>
    public AneiangYarpPluginMiddleware(
        RequestDelegate next,
        ILogger<AneiangYarpPluginMiddleware> logger,
        IGatewaySnapshotPublisher snapshots)
    {
        _next = next;
        _logger = logger;
        _snapshots = snapshots;
    }

    /// <summary>Handles one proxied request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // A plugin in the proxy pipeline always runs with a matched route, but the feature is read
        // through the interface rather than assumed so an unexpected host shape degrades instead of
        // throwing on every request.
        var routeId = context.Features.Get<IReverseProxyFeature>()?.Route?.Config?.RouteId;

        // No binding on this route is not an error: the plugin is still in the pipeline, so it
        // stamps the defaults. That is what makes "is the plugin running?" answerable before
        // anyone has configured anything.
        var config = ReadConfig(routeId);
        if (!config.Enabled)
        {
            await _next(context);
            return;
        }

        // Headers can only be added until the first byte is written. OnStarting runs at exactly
        // that point; assigning afterwards is either dropped or throws, depending on the response.
        context.Response.OnStarting(() =>
        {
            ApplyHeader(context, config);
            return Task.CompletedTask;
        });

        await _next(context);
    }

    /// <summary>Returns this plugin's binding on the route, or the defaults when there is none.</summary>
    private AneiangYarpPluginConfig ReadConfig(string? routeId)
    {
        if (string.IsNullOrEmpty(routeId) ||
            !_snapshots.Current.RoutePlugins.TryGetValue(routeId, out var bindings))
        {
            return new AneiangYarpPluginConfig();
        }

        foreach (var binding in bindings)
        {
            if (!string.Equals(binding.PluginId, AneiangYarpPluginEntry.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            // An empty ConfigJson is a binding that was saved without settings; the defaults apply.
            if (string.IsNullOrWhiteSpace(binding.ConfigJson))
                return new AneiangYarpPluginConfig();

            try
            {
                return JsonSerializer.Deserialize<AneiangYarpPluginConfig>(binding.ConfigJson, ConfigJsonOptions)
                    ?? new AneiangYarpPluginConfig();
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception,
                    "Binding {BindingId} on route {RouteId} holds configuration this plugin cannot read; using defaults",
                    binding.BindingId, routeId);
                return new AneiangYarpPluginConfig();
            }
        }

        return new AneiangYarpPluginConfig();
    }

    /// <summary>Writes the header, skipping the values a misconfigured binding may contain.</summary>
    private void ApplyHeader(HttpContext context, AneiangYarpPluginConfig config)
    {
        var name = config.HeaderName?.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        try
        {
            context.Response.Headers[name] = config.HeaderValue ?? string.Empty;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // A header name typed by a human can be invalid. Skipping it keeps the request alive;
            // failing here would turn a typo in the dashboard into a 500.
            _logger.LogDebug(exception, "Skipping invalid response header '{HeaderName}'", name);
        }
    }
}
