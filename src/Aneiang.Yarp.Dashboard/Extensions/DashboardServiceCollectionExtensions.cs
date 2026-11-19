using System.Collections.Concurrent;
using Aneiang.Yarp.Dashboard.Infrastructure;
using Aneiang.Yarp.Dashboard.Infrastructure.Auth;
using Aneiang.Yarp.Dashboard.Infrastructure.HostedServices;
using Aneiang.Yarp.Dashboard.Infrastructure.State;
using Aneiang.Yarp.Dashboard.Infrastructure.Performance;
using Aneiang.Yarp.Infrastructure.Performance;
using Aneiang.Yarp.Plugins.CircuitBreaker.Services;
using Aneiang.Yarp.Dashboard.Infrastructure.Deployment;
using Aneiang.Yarp.Dashboard.Infrastructure.Plugin;
using Aneiang.Yarp.Dashboard.Infrastructure.Policies;
using Aneiang.Yarp.Dashboard.Infrastructure.Realtime;
using Aneiang.Yarp.Dashboard.Infrastructure.Security;
using Aneiang.Yarp.Dashboard.Modules.Dashboard.Controllers;
using Aneiang.Yarp.Dashboard.Modules.Dashboard.Services;
using Aneiang.Yarp.Dashboard.Modules.Environments;
using Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;
using Aneiang.Yarp.Plugin.Cache;
using Aneiang.Yarp.Plugin.CircuitBreaker;
using Aneiang.Yarp.Plugin.Compression;
using Aneiang.Yarp.Plugin.FaultInjection;
using Aneiang.Yarp.Plugin.Metrics;
using Aneiang.Yarp.Plugin.ProxyLog;
using Aneiang.Yarp.Plugin.ProxyLog.Services;
using Aneiang.Yarp.Plugin.RateLimit;
using Aneiang.Yarp.Plugin.Retry;
using Aneiang.Yarp.Plugin.ServiceDiscovery;
using Aneiang.Yarp.Plugin.Waf;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;
using Aneiang.Yarp.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IO;
using Yarp.ReverseProxy.Transforms.Builder;
using Aneiang.Yarp.Infrastructure.State;
using Aneiang.Yarp.Infrastructure.Resilience;
using Aneiang.Yarp.Plugins;

namespace Aneiang.Yarp.Dashboard.Extensions;

/// <summary>Aneiang.Yarp.Dashboard service registration extensions.</summary>
public static class DashboardServiceCollectionExtensions
{
    /// <summary>
    /// Register Dashboard with configurable auth and route prefix.
    /// Options are bound from <c>Gateway:Dashboard</c> configuration section.
    /// </summary>
    public static IServiceCollection AddAneiangYarpDashboard(
        this IServiceCollection services,
        Action<DashboardOptions>? configureOptions = null)
    {
        services.AddDashboardOptions(configureOptions);
        services.AddDashboardWebInfrastructure();
        services.AddDashboardStorageAndAudit();
        services.AddDashboardSecurity();
        services.AddDashboardProxyLog();
        services.AddDashboardQueryServices();
        services.AddDashboardNotificationAndPlugins();
        services.AddDashboardRealtimeAndPerformance();
        services.AddDashboardConfigPersistence();
        services.AddDashboardAI();
        services.AddDashboardWarmupServices();
        services.AddDashboardTracing();

        // Named policies referenced by route AuthorizationPolicy / CorsPolicy /
        // RateLimiterPolicy / OutputCachePolicy. Without these the route form asked for a
        // policy name that could never resolve, and the failure surfaced at request time.
        services.AddGatewayPolicies();

        // Plugins that contribute host-scoped YARP extension points (LB policies, transforms).
        // Runs last so every built-in plugin is already registered and can be inspected.
        services.AddPluginHostServiceContributions();

        return services;
    }

    #region Option binding

    private static IServiceCollection AddDashboardOptions(
        this IServiceCollection services,
        Action<DashboardOptions>? configureOptions)
    {
        services.AddOptions<DashboardOptions>()
            .BindConfiguration(DashboardOptions.SectionName)
            .Configure<IConfiguration>((options, config) =>
            {
                var controlPlane = config.GetSection(ControlPlaneSecurityOptions.SectionName).Get<ControlPlaneSecurityOptions>();
                if (controlPlane == null || string.IsNullOrWhiteSpace(controlPlane.AuthMode)) return;

                // Explicit Dashboard auth config still wins. Unified control-plane config fills only when Dashboard auth is None.
                if (options.Auth.AuthMode != DashboardAuthMode.None || options.Auth.AuthorizeRequest != null) return;

                if (string.Equals(controlPlane.AuthMode, "ApiKey", StringComparison.OrdinalIgnoreCase))
                {
                    options.Auth.AuthMode = DashboardAuthMode.ApiKey;
                    options.Auth.ApiKey = controlPlane.ApiKey;
                    options.Auth.ApiKeyHeaderName = string.IsNullOrWhiteSpace(controlPlane.ApiKeyHeaderName) ? "X-Api-Key" : controlPlane.ApiKeyHeaderName;
                }
                else if (string.Equals(controlPlane.AuthMode, "CustomJwt", StringComparison.OrdinalIgnoreCase))
                {
                    options.Auth.AuthMode = DashboardAuthMode.CustomJwt;
                    options.Auth.JwtUsername = controlPlane.Username ?? "admin";
                    options.Auth.JwtPassword = controlPlane.Password;
                }
                else if (string.Equals(controlPlane.AuthMode, "DefaultJwt", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(controlPlane.AuthMode, "BasicAuth", StringComparison.OrdinalIgnoreCase))
                {
                    options.Auth.AuthMode = DashboardAuthMode.DefaultJwt;
                    options.Auth.JwtPassword = controlPlane.Password;
                }
            });

        // Sub-options: Auth and ProxyLog can be injected independently
        services.AddOptions<DashboardAuthOptions>()
            .BindConfiguration("Gateway:Dashboard:Auth");
        services.AddOptions<ProxyLogOptions>()
            .BindConfiguration("Gateway:Dashboard:ProxyLog");

        services.AddOptions<ConfigHistoryOptions>()
            .BindConfiguration(ConfigHistoryOptions.SectionName)
            .PostConfigure(options =>
            {
                options.MaxSnapshots = Math.Max(1, options.MaxSnapshots);
                options.SnapshotQueueCapacity = Math.Max(1, options.SnapshotQueueCapacity);
            });

        // Deployment options — BindConfiguration provides raw config; AddAneiangYarpDeployment
        // (if called) will PostConfigure to normalize Mode (Auto→Split/AllInOne).
        services.AddOptions<DeploymentOptions>()
            .BindConfiguration(DeploymentOptions.SectionName);

        // AI assistant options — binds from top-level "AI" section
        services.AddOptions<AIOptions>()
            .BindConfiguration(AIOptions.SectionName);

        // Canary rollout defaults. Binds the documented section, then clamps so a typo in appsettings
        // cannot produce a rollout that never evaluates (window 0) or never trips (threshold 0).
        services.AddOptions<Modules.Canary.Models.CanaryOptions>()
            .BindConfiguration(Modules.Canary.Models.CanaryOptions.SectionName)
            .PostConfigure(options =>
            {
                options.ObservationWindowSeconds = Math.Clamp(options.ObservationWindowSeconds, 10, 7200);
                options.TickSeconds = Math.Clamp(options.TickSeconds, 1, 60);
                options.MaxWindowExtensions = Math.Clamp(options.MaxWindowExtensions, 0, 20);
                options.MinSamples = Math.Clamp(options.MinSamples, 1, 1_000_000);
                options.MaxRetainedRollouts = Math.Clamp(options.MaxRetainedRollouts, 1, 500);
                if (options.ErrorRateThreshold is <= 0 or > 1)
                    options.ErrorRateThreshold = 0.2;
                if (options.DefaultSteps is not { Length: > 0 })
                    options.DefaultSteps = new[] { 10, 30, 50, 100 };
            });

        // Metric alert thresholds. Clamped for the same reason as the canary options: a window of 0
        // or a threshold of 0 produces a rule that either never evaluates or fires on everything.
        services.AddOptions<Modules.Alerting.Models.MetricAlertOptions>()
            .BindConfiguration(Modules.Alerting.Models.MetricAlertOptions.SectionName)
            .PostConfigure(options =>
            {
                options.TickSeconds = Math.Clamp(options.TickSeconds, 5, 300);
                options.MaxIncidents = Math.Clamp(options.MaxIncidents, 10, 5000);
                options.DefaultWindowSeconds = Math.Clamp(options.DefaultWindowSeconds, 10, 3600);
                options.DefaultForSeconds = Math.Clamp(options.DefaultForSeconds, 0, 3600);
                options.DefaultMinSamples = Math.Clamp(options.DefaultMinSamples, 1, 1_000_000);
                if (options.DefaultErrorRateThreshold is <= 0 or > 1)
                    options.DefaultErrorRateThreshold = 0.05;
                if (options.DefaultLatencyP99Seconds is <= 0 or > 3600)
                    options.DefaultLatencyP99Seconds = 1.0;
            });

        services.AddSingleton<Modules.Alerting.Services.MetricAlertStore>();
        services.AddSingleton<Modules.Alerting.Services.IMetricAlertService,
            Modules.Alerting.Services.MetricAlertService>();
        services.AddHostedService<Modules.Alerting.Services.MetricAlertSupervisor>();

        // Alert pipeline: gateway alerts flow into the notification dispatcher,
        // which fans out to subscribed webhook platforms with cooldown + retry.
        services.AddSingleton<Infrastructure.Notifications.NotificationDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<Infrastructure.Notifications.NotificationDispatcher>());
        services.AddSingleton<Infrastructure.Notifications.WebhookAlertService>();
        services.AddSingleton<Aneiang.Yarp.Dashboard.Infrastructure.Alert.IGatewayAlertService,
            Aneiang.Yarp.Dashboard.Infrastructure.Notifications.WebhookAlertService>();

        if (configureOptions != null)
            services.Configure(configureOptions);

        return services;
    }

    #endregion

    #region Distributed tracing

    private static IServiceCollection AddDashboardTracing(this IServiceCollection services)
    {
        // Off by default: when disabled no tracer provider and no exporter are created at all, so
        // there is nothing trying to reach a collector nobody configured.
        services.AddOptions<Modules.Tracing.Models.GatewayTracingOptions>()
            .BindConfiguration(Modules.Tracing.Models.GatewayTracingOptions.SectionName)
            .PostConfigure(options =>
            {
                options.RecentTraceCapacity = Math.Clamp(options.RecentTraceCapacity, 1, 1000);
                options.MaxSpansPerTrace = Math.Clamp(options.MaxSpansPerTrace, 4, 500);

                // A ratio outside 0..1 means "sample nothing" or "sample everything" depending on
                // which side the comparison falls; NaN means neither. All three are typos.
                if (double.IsNaN(options.SamplingRatio) || options.SamplingRatio is < 0 or > 1)
                    options.SamplingRatio = 0.1;

                if (string.IsNullOrWhiteSpace(options.ServiceName))
                    options.ServiceName = "aneiang-gateway";
                if (string.IsNullOrWhiteSpace(options.Protocol))
                    options.Protocol = "grpc";
            });

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<Modules.Tracing.Models.GatewayTracingOptions>>().Value);

        // The buffer size comes from options, so it cannot be a plain AddSingleton<T>().
        services.AddSingleton(sp =>
        {
            var tracing = sp.GetRequiredService<IOptions<Modules.Tracing.Models.GatewayTracingOptions>>().Value;
            return new Modules.Tracing.Services.TraceRingBuffer(
                Modules.Tracing.Services.GatewayTracingHost.BufferSources,
                tracing.RecentTraceCapacity,
                tracing.MaxSpansPerTrace);
        });

        services.AddSingleton(sp => new Modules.Tracing.Services.RuntimeRatioSampler(
            sp.GetRequiredService<IOptions<Modules.Tracing.Models.GatewayTracingOptions>>().Value.SamplingRatio));

        // Owns the panel-written credential file. Registered before the host so the host can merge
        // the panel's headers into what it hands the exporter.
        services.AddSingleton(sp => new Modules.Tracing.Services.TracingAuthStore(
            sp.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>().ContentRootPath,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Modules.Tracing.Services.TracingAuthStore>>()));

        // Registered twice on purpose: the hosted service starts the provider, and the tracing API
        // reads IsRunning / StartError off the same instance.
        services.AddSingleton<Modules.Tracing.Services.GatewayTracingHost>();
        services.AddHostedService(sp => sp.GetRequiredService<Modules.Tracing.Services.GatewayTracingHost>());
        services.AddSingleton<Modules.Tracing.Services.IGatewayTracingService,
            Modules.Tracing.Services.GatewayTracingService>();

        // Null path: named client with only the probe's own linked timeout. Registered separately
        // from the other named clients so a future change to their handlers cannot alter what the
        // probe measures.
        services.AddHttpClient(Modules.Tracing.Services.TracingExportProbe.HttpClientName);
        services.AddSingleton<Modules.Tracing.Services.TracingExportProbe>();

        return services;
    }

    #endregion

    #region Web infrastructure (MVC, Razor, SignalR, compression)

    private static IServiceCollection AddDashboardWebInfrastructure(this IServiceCollection services)
    {
        // MVC controllers with JSON camelCase naming policy.
        // Comments and trailing commas are tolerated so route/cluster editors and config import
        // accept relaxed JSON (matching docs/yarp_all.json style).
        services.AddMvcCore()
            .AddApplicationPart(typeof(DashboardPagesController).Assembly)
            .AddApplicationPart(typeof(Aneiang.Yarp.Plugins.CircuitBreaker.Controllers.CircuitBreakerController).Assembly)
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip;
                options.JsonSerializerOptions.AllowTrailingCommas = true;
            });

        // Let DashboardAuth/DashboardPages controllers also search Views/Dashboard/
        services.Configure<RazorViewEngineOptions>(o =>
            o.ViewLocationExpanders.Add(new DashboardViewLocationExpander()));

        services.AddMemoryCache();
        services.AddSignalR();

        // Response Compression: Brotli (preferred) + Gzip fallbacks.
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = new[]
            {
                "text/plain", "text/css", "text/javascript", "text/xml",
                "application/javascript", "application/json", "application/xml",
                "application/xml-dtd", "application/atom+xml", "application/octet-stream",
                "image/svg+xml", "font/woff", "font/woff2", "font/ttf",
                "application/wasm",
            };
        });
        services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = System.IO.Compression.CompressionLevel.Optimal;
        });
        services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = System.IO.Compression.CompressionLevel.Optimal;
        });

        return services;
    }

    #endregion

    #region Storage, audit, rate limiting

    private static IServiceCollection AddDashboardStorageAndAudit(this IServiceCollection services)
    {
        // Storage backend (e.g. AddAneiangStorage) is registered by the host application.
        // Dashboard only depends on Aneiang.Yarp.Storage.Abstractions interfaces.

        // DynamicYarpConfigService loads config from storage on StartAsync.
        // Schema migration is triggered lazily by the connection factory on first
        // connection use, so tables are guaranteed to exist regardless of registration order.
        services.AddHostedService(sp => sp.GetRequiredService<Aneiang.Yarp.Services.DynamicYarpConfigService>());

        services.AddSingleton<IConfigChangeAuditLog, ConfigChangeAuditLog>();
        services.AddSingleton<ConfigChangeAuditLog>(sp => (ConfigChangeAuditLog)sp.GetRequiredService<IConfigChangeAuditLog>());
        services.AddSingleton<ConfigChangeEventDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<ConfigChangeEventDispatcher>());

        // In-memory state stores (singleton to share state across middleware instances)
        services.AddSingleton<ICircuitStateStore, InMemoryCircuitStateStore>();
        services.AddSingleton<Aneiang.Yarp.Infrastructure.Resilience.IDestinationCandidateCoordinator, Infrastructure.Resilience.DestinationCandidateCoordinator>();
        services.AddSingleton<IRateLimiterStore, InMemoryRateLimiterStore>();

        return services;
    }

    #endregion

    #region Security (Gateway API auth, JWT, MVC conventions)

    private static IServiceCollection AddDashboardSecurity(this IServiceCollection services)
    {
        Aneiang.Yarp.Extensions.AneiangYarpServiceCollectionExtensions.AddGatewayApiAuth(services);
        services.AddSingleton<GatewayApiAuthFilter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<MvcOptions>, GatewayApiAuthMvcOptionsSetup>());

        services.AddSingleton<IDashboardAuthorizationService, DashboardAuthorizationService>();
        services.AddSingleton<JwtSecretProvider>();
        services.AddSingleton<IConfigureOptions<MvcOptions>, DashboardMvcOptionsSetup>();

        // Security events go to the same audit table as config changes, under their own target
        // type. Registered here rather than with the storage backend because auth is what needs
        // them, and storage is optional.
        services.TryAddSingleton<ISecurityAuditWriter, SecurityAuditWriter>();

        // Login budget: per account and per address, in this process. Single control plane is the
        // documented deployment (ADR-1), so a shared cache would be a promise the code cannot keep.
        services.TryAddSingleton<LoginThrottle>();

        // A JWT cannot be un-signed, so early invalidation needs a list of ids. In-memory, and that
        // limitation is documented on the type rather than hidden.
        services.TryAddSingleton<DashboardTokenRevocationStore>();

        return services;
    }

    #endregion

    #region Proxy log store + persistence

    private static IServiceCollection AddDashboardProxyLog(this IServiceCollection services)
    {
        // Register ProxyLogPluginOptions mapped from DashboardOptions.ProxyLog
        services.AddSingleton<IConfigureOptions<ProxyLogPluginOptions>>(sp =>
        {
            return new ConfigureNamedOptions<ProxyLogPluginOptions>(Options.DefaultName, pluginOpts =>
            {
                var dashboardOpts = sp.GetRequiredService<IOptions<DashboardOptions>>().Value;
                var logOpts = dashboardOpts.ProxyLog;
                pluginOpts.LogPersistenceEnabled = logOpts.LogPersistenceEnabled;
                pluginOpts.LogMetaRetentionDays = logOpts.LogMetaRetentionDays;
                pluginOpts.LogBodyRetentionDays = logOpts.LogBodyRetentionDays;
                pluginOpts.EnableProxyRequestBodyCapture = logOpts.EnableProxyRequestBodyCapture;
                pluginOpts.EnableProxyResponseBodyCapture = logOpts.EnableProxyResponseBodyCapture;
                pluginOpts.LogMaxBodyLength = logOpts.LogMaxBodyLength;
                pluginOpts.LogMaxBodyBufferBytes = logOpts.LogMaxBodyBufferBytes;
                pluginOpts.EnableLogSampling = logOpts.EnableLogSampling;
                pluginOpts.LogSamplingRate = logOpts.LogSamplingRate;
                pluginOpts.LogErrorsOnly = logOpts.LogErrorsOnly;
                pluginOpts.MinLogLevel = logOpts.MinLogLevel;
                pluginOpts.LogBufferCapacity = logOpts.LogBufferCapacity;
                pluginOpts.LogHeaderBlacklist = logOpts.LogHeaderBlacklist ?? [];
                pluginOpts.LogQueryBlacklist = logOpts.LogQueryBlacklist ?? [];
                pluginOpts.LogJsonFieldSanitizeList = logOpts.LogJsonFieldSanitizeList ?? [];
            });
        });

        services.TryAddSingleton<ProxyLogRuntimeSettings>();
        services.AddSingleton<LogSettingsService>();
        services.AddHostedService(sp => sp.GetRequiredService<LogSettingsService>());
        services.AddSingleton<IProxyLogStore>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<DashboardOptions>>().Value;
            var runtimeSettings = sp.GetRequiredService<ProxyLogRuntimeSettings>();
            return new ProxyLogStore(
                opts.ProxyLog.LogBufferCapacity,
                persistenceEnabled: opts.ProxyLog.LogPersistenceEnabled,
                runtimeSettings: runtimeSettings);
        });
        services.AddSingleton<ProxyLogStore>(sp => (ProxyLogStore)sp.GetRequiredService<IProxyLogStore>());
        services.AddSingleton<LogSanitizer>();
        services.AddSingleton<ILogSanitizer>(sp => sp.GetRequiredService<LogSanitizer>());

        // Log sampling + filtering (extracted from YarpRequestCaptureMiddleware)
        services.AddSingleton<ILogSampler, LogSampler>();
        services.AddSingleton<ILogFilter, LogFilter>();
        services.AddSingleton<IProxyLogCapture, ProxyLogCapture>();

        // SqliteProxyLogWriter: converts LogEntry → Entity and delegates to IProxyLogRepository
        services.AddSingleton<SqliteProxyLogWriter>();
        // AsyncLogPersistenceService: background service that reads from Channel and writes batches to SQLite
        services.AddSingleton<AsyncLogPersistenceService>();
        services.AddSingleton<IProxyLogPersistenceService>(sp => sp.GetRequiredService<AsyncLogPersistenceService>());
        services.AddSingleton<IPluginRuntimeResource>(sp => sp.GetRequiredService<AsyncLogPersistenceService>());

        return services;
    }

    #endregion

    #region Query services + editable policy

    private static IServiceCollection AddDashboardQueryServices(this IServiceCollection services)
    {
        services.AddSingleton<IDashboardInfoQueryService, DashboardInfoQueryService>();
        services.AddSingleton<IDashboardClusterQueryService, DashboardClusterQueryService>();
        services.AddSingleton<IDashboardRouteQueryService, DashboardRouteQueryService>();
        services.AddSingleton<IDashboardLogQueryService, DashboardLogQueryService>();

        return services;
    }

    #endregion

    #region Plugin host services

    /// <summary>
    /// Invites plugins to contribute services to the <em>host</em> container.
    /// </summary>
    /// <remarks>
    /// <see cref="IGatewayPlugin.ConfigureServices"/> runs against the per-plugin runtime domain,
    /// which is built when the plugin is enabled — after the host container is frozen. Anything YARP
    /// resolves by enumeration while starting up (<c>ILoadBalancingPolicy</c>,
    /// <c>ITransformProvider</c>) therefore has to be registered here, or it is silently ignored.
    /// </remarks>
    private static IServiceCollection AddPluginHostServiceContributions(this IServiceCollection services)
    {
        var pluginTypes = services
            .Where(d => d.ServiceType == typeof(IGatewayPlugin) && d.ImplementationType is not null)
            .Select(d => d.ImplementationType!)
            .ToArray();

        // The scan itself lives in the abstractions package so a host that composes plugins without
        // the dashboard gets the same behaviour instead of a JSON-only plugin model.
        services.AddPluginHostServices(pluginTypes);

        return services;
    }

    #endregion

    #region Notification + plugin system

    private static IServiceCollection AddDashboardNotificationAndPlugins(this IServiceCollection services)
    {
        // Notification module removed in Phase 6 cleanup.

        services.AddSingleton<IGatewayPlugin, CircuitBreakerPlugin>();
        services.AddSingleton<IGatewayPlugin, RequestRetryPlugin>();
        services.AddSingleton<IGatewayPlugin, RateLimitPlugin>();
        services.AddSingleton<IGatewayPlugin, WafPlugin>();
        services.AddSingleton<IGatewayPlugin, ProxyLogPlugin>();
        services.AddSingleton<IGatewayPlugin, ResponseCachePlugin>();
        services.AddSingleton<IGatewayPlugin, TrafficMetricsPlugin>();
        services.AddSingleton<IGatewayPlugin, ClusterMetricsPlugin>();
        services.AddSingleton<IGatewayPlugin, HttpServiceDiscoveryPlugin>();
        services.AddSingleton<IGatewayPlugin, CompressionPlugin>();
        services.AddSingleton<IGatewayPlugin, FaultInjectionPlugin>();
        services.AddSingleton<PluginMetricStore>();
        services.AddSingleton<ExternalGatewayPluginHost>();
        services.AddSingleton<GatewayPluginManager>();
        services.AddSingleton<IGatewayPluginManager>(provider => provider.GetRequiredService<GatewayPluginManager>());
        services.AddSingleton<IPluginDashboardBuilder, PluginDashboardBuilder>();
        services.AddSingleton<PluginDashboardInitializer>();
        services.AddSingleton<IPluginRuntimeDomainManager, PluginRuntimeDomainManager>();
        services.AddSingleton<IPluginBindingMutationService, PluginBindingMutationService>();
        services.AddSingleton(provider => (PluginRuntimeDomainManager)provider.GetRequiredService<IPluginRuntimeDomainManager>());
        services.AddHostedService<PluginRuntimeDomainInitializer>();
        services.AddSingleton<IPluginActivationState>(provider => provider.GetRequiredService<GatewayPluginManager>());
        services.AddSingleton<GatewayPluginExecutionPlanProvider>();
        services.AddSingleton<IPluginManifestCatalog>(provider => provider.GetRequiredService<IGatewayPluginManager>());
        services.AddSingleton<IPluginConfigurationSchemaValidator, PluginConfigurationSchemaValidator>();
        services.AddSingleton<IPluginConfigurationMigrationService, PluginConfigurationMigrationService>();
        services.AddSingleton<IPluginConfigurationMigrator, CircuitBreakerConfigMigrator>();
        services.AddSingleton<IPluginConfigurationMigrator, RetryConfigMigrator>();
        services.AddSingleton<IPluginConfigurationMigrator, WafConfigMigrator>();
        services.AddSingleton<IPluginConfigurationMigrator, RateLimitConfigMigrator>();
        services.AddSingleton<IPluginConfigurationMigrator, ProxyLogConfigMigrator>();
        services.AddSingleton<CircuitBreakerWarmupService>();
        services.AddSingleton<IPluginRuntimeResource>(provider => provider.GetRequiredService<CircuitBreakerWarmupService>());
        services.AddSingleton<IPluginRuntimeResource, ServiceDiscoveryRefreshService>();
        services.AddHttpClient("service-discovery");
        services.AddSingleton<PluginResourceLifecycleCoordinator>();
        services.AddSingleton<IPluginResourceLifecycleCoordinator>(provider => provider.GetRequiredService<PluginResourceLifecycleCoordinator>());
        services.AddHostedService(provider => provider.GetRequiredService<PluginResourceLifecycleCoordinator>());
        services.AddSingleton<PluginResourceMonitor>();
        services.AddSingleton<IPluginResourceMonitor>(provider => provider.GetRequiredService<PluginResourceMonitor>());

        return services;
    }

    #endregion

    #region Real-time, performance, statistics

    private static IServiceCollection AddDashboardRealtimeAndPerformance(this IServiceCollection services)
    {
        services.AddSingleton<TrafficBroadcastService>();
        services.AddHostedService<TrafficBroadcastService>();

        services.AddSingleton<OverviewBroadcastService>();
        services.AddHostedService<OverviewBroadcastService>();
        services.AddSingleton<Infrastructure.Realtime.IOverviewSnapshotProvider, Infrastructure.Realtime.OverviewSnapshotProvider>();

        // Webhook notifications (DingTalk / generic endpoints for config-change events).
        // Redirects are not followed: the SSRF guard checks the URL that was configured, and a 302
        // to http://169.254.169.254/ would arrive at the same place without ever being checked.
        services.AddHttpClient("webhook")
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<Infrastructure.Notifications.WebhookNotificationService>();

        services.AddSingleton<RecyclableMemoryStreamManager>();
        services.AddSingleton<LockFreeStatistics>();
        services.AddSingleton<ITransformProvider, DownstreamCaptureTransformProvider>();

        // Prometheus exposition: recorded in the proxy pipeline, rendered on demand.
        services.AddSingleton<Infrastructure.Observability.GatewayMetricsCollector>();
        services.AddSingleton<Infrastructure.Observability.UpstreamConnectionMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<Infrastructure.Observability.UpstreamConnectionMonitor>());
        services.AddSingleton<Infrastructure.Observability.GatewayMetricsRenderer>();

        // Staged rollouts. The supervisor is the only thing that closes an observation window, and
        // it runs in-process because the metric counters it reads are per-instance — a rollout on
        // three gateway nodes is three rollouts, which is why the docs say so out loud.
        services.AddSingleton<Modules.Canary.Services.CanaryLedgerStore>();
        services.AddSingleton<Modules.Canary.Services.ICanaryRolloutService, Modules.Canary.Services.CanaryRolloutService>();
        services.AddHostedService<Modules.Canary.Services.CanaryRolloutSupervisor>();

        return services;
    }

    #endregion

    #region Config persistence + identity + health

    private static IServiceCollection AddDashboardConfigPersistence(this IServiceCollection services)
    {
        services.AddSingleton<ConfigPersistenceService>();
        services.AddSingleton<IConfigPersistenceService>(sp => sp.GetRequiredService<ConfigPersistenceService>());
        services.AddSingleton<IConfigDiffService, ConfigDiffService>();

        // Cross-environment comparison and promotion. Registration is unconditional; the endpoints
        // refuse with a clear message when only one environment is configured.
        services.AddSingleton<EnvironmentDiffService>();
        services.AddSingleton<EnvironmentPromotionService>();
        services.AddSingleton<IEnvironmentReloader, EnvironmentReloader>();
        services.AddSingleton<IPromotionAudit, PromotionAudit>();

        services.AddSingleton<ConfigSnapshotScheduler>();
        services.AddSingleton<IConfigSnapshotScheduler>(sp => sp.GetRequiredService<ConfigSnapshotScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<ConfigSnapshotScheduler>());
        services.AddSingleton<IGatewayIdentityService, GatewayIdentityService>();

        // Pre-flight checks: validate-before-save + whole-config integrity.
        services.AddSingleton<IConfigurationValidator, ConfigurationValidator>();

        // Swap the core library's permissive guard for the real validator, so every write path
        // — dashboard forms, client self-registration, batch import, history rollback, AI tools —
        // goes through the same checks. Replace (not Add) because the core registered a default.
        services.Replace(ServiceDescriptor.Singleton<IConfigurationGuard, ConfigurationValidatorGuard>());

        return services;
    }

    #endregion

    #region AI assistant

    private static IServiceCollection AddDashboardAI(this IServiceCollection services)
    {
        services.AddSingleton<AIConfigStore>();
        services.AddSingleton<AIFunctionService>();
        services.AddSingleton<AIService>();

        // AI-enhanced notifications - decorates the webhook alert service so each
        // alert gets an LLM-generated context/suggestion when AI.EnhanceNotif is on.
        services.Replace(ServiceDescriptor.Singleton<
            Aneiang.Yarp.Dashboard.Infrastructure.Alert.IGatewayAlertService>(sp =>
            new AIEnhancedAlertService(
                sp.GetRequiredService<Infrastructure.Notifications.WebhookAlertService>(),
                sp.GetRequiredService<AIConfigStore>(),
                sp,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AIEnhancedAlertService>>())));

        // MCP server - exposes gateway read tools to external AI clients (Streamable HTTP).
        services.AddHostedService<McpServerHost>();

        return services;
    }

    #endregion

    #region Warmup services

    private static IServiceCollection AddDashboardWarmupServices(this IServiceCollection services)
    {
        services.AddBackgroundHostedService<StartupWarmupService>();
        return services;
    }

    #endregion

    /// <summary>
    /// Register deployment-related services (EndpointRoleResolver, config validators, snapshot store, hot-reload).
    /// Configuration is resolved from DI, so this can be called as <c>services.AddAneiangYarpDeployment()</c>.
    /// </summary>
    public static IServiceCollection AddAneiangYarpDeployment(this IServiceCollection services)
    {
        services.AddOptions<DeploymentOptions>()
            .BindConfiguration(DeploymentOptions.SectionName);


        services.TryAddSingleton<DeploymentRestartState>();
        services.TryAddSingleton<EndpointRoleResolver>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var options = sp.GetRequiredService<IOptions<DeploymentOptions>>().Value;
            options.ResolvedEndpoints.Clear();
            return new EndpointRoleResolver(configuration, options);
        });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DeploymentConfigValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Aneiang.Yarp.Dashboard.Infrastructure.HostedServices.KestrelEndpointChangeDetector>());
        return services;
    }

}
