using Aneiang.Yarp.GatewayRegistry;
using GatewayGrpc = Aneiang.Yarp.GatewayRegistry.GatewayRegistry;
using Aneiang.Yarp.Models;
using Aneiang.Yarp.Services;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Extensions;

/// <summary>
/// Aneiang.Yarp client service registration extensions.
/// </summary>
public static class AneiangYarpClientServiceCollectionExtensions
{
    /// <summary>
    /// Register this service as a YARP client with auto-registration to the gateway.
    /// Auto-registers route on startup, auto-unregisters on shutdown.
    /// The only required config is <c>GatewayUrl</c> (code or appsettings.json).
    /// </summary>
    /// <param name="services">IServiceCollection</param>
    /// <param name="configureOptions">Optional override for registration options.</param>
    /// <example>
    /// <code>
    /// // Minimal - only config file needed:
    /// builder.Services.AddAneiangYarpClient();
    /// // { "Gateway:Registration": { "GatewayUrl": "http://192.168.1.100:5000" } }
    ///
    /// // With code override:
    /// builder.Services.AddAneiangYarpClient(o => {
    ///     o.GatewayUrl = "http://gateway:5000";
    ///     o.MatchPath = "/api/users/{**catch-all}";
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddAneiangYarpClient(
        this IServiceCollection services,
        Action<GatewayRegistrationOptions>? configureOptions = null)
    {
        ConfigureClientInfrastructure(services, configureOptions);
        services.AddSingleton<GatewayAutoRegistrationClient>();
        services.AddHostedService<GatewayRegistrationHostedService>();
        return services;
    }

    /// <summary>
    /// Register client services without the hosted service (used by gateway to support upstream registration).
    /// </summary>
    public static IServiceCollection AddAneiangYarpClientInternal(
        this IServiceCollection services)
    {
        ConfigureClientInfrastructure(services, null);
        services.AddSingleton<GatewayAutoRegistrationClient>();
        return services;
    }

    private static void ConfigureClientInfrastructure(
        IServiceCollection services,
        Action<GatewayRegistrationOptions>? configureOptions)
    {
        services.AddHttpClient();
        ConfigureRegistrationOptions(services, configureOptions);
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GatewayRegistrationOptions>>().Value;
            var gatewayUrl = options.GatewayUrl ?? "http://localhost:5000";

            // gRPC over HTTP requires HTTP/2, but .NET 9 Kestrel does not support
            // HTTP/1.1 + HTTP/2 on the same cleartext port. Use a dedicated HTTP/2 port.
            // Priority: options.GrpcPort > Gateway:Grpc:Port config > port+1 fallback.
            // See UseYarpKestrelAutoConfig / KestrelExtensions.TryParseAndConfigure.
            if (Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gwUri) &&
                gwUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                var grpcPort = options.GrpcPort ?? (gwUri.Port + 1);
                gatewayUrl = $"{gwUri.Scheme}://{gwUri.Host}:{grpcPort}{gwUri.PathAndQuery}";
            }

            // Certificate validation used to be disabled unconditionally here, for self-signed and
            // ASP.NET dev certificates. That is the whole check that this channel reaches the
            // gateway it intends to, so it is now an explicit opt-in (SEC-17 / CA5359). A plain
            // http:// gateway never reaches this branch — there is no certificate to validate.
            var handler = CreateGrpcHandler(options.AllowUntrustedGrpcCertificate);

            return GrpcChannel.ForAddress(gatewayUrl, new GrpcChannelOptions
            {
                HttpHandler = handler
            });
        });
        services.AddSingleton(sp =>
        {
            var channel = sp.GetRequiredService<GrpcChannel>();
            return new GatewayGrpc.GatewayRegistryClient(channel);
        });
        services.AddSingleton<KestrelAutoConfigService>();
    }

    /// <summary>
    /// Builds the HTTP handler for the gRPC registration channel.
    /// </summary>
    /// <param name="allowUntrustedCertificate">
    /// When true, TLS certificate validation is skipped. The caller passes
    /// <see cref="GatewayRegistrationOptions.AllowUntrustedGrpcCertificate"/>, which defaults to
    /// false so the validating path is what a deployment gets without asking.
    /// </param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security", "CA5359:Do not disable certificate validation",
        Justification = "The non-validating branch is reachable only when Gateway:Registration:AllowUntrustedGrpcCertificate is explicitly true; the default path returns a handler that validates normally.")]
    private static SocketsHttpHandler CreateGrpcHandler(bool allowUntrustedCertificate)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        };

        if (!allowUntrustedCertificate)
            return handler;

        handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        };

        return handler;
    }

    internal static void ConfigureRegistrationOptions(
        IServiceCollection services,
        Action<GatewayRegistrationOptions>? configureOptions)
    {
        services.AddOptions<GatewayRegistrationOptions>()
            .Configure<IConfiguration>((o, c) =>
                c.GetSection(GatewayRegistrationOptions.SectionName).Bind(o));

        if (configureOptions != null)
            services.Configure(configureOptions);

        services.AddSingleton<IValidateOptions<GatewayRegistrationOptions>, SkipValidation>();
    }

    /// <summary>Skips options validation (all config is optional).</summary>
    internal sealed class SkipValidation : IValidateOptions<GatewayRegistrationOptions>
    {
        public ValidateOptionsResult Validate(string? name, GatewayRegistrationOptions options)
            => ValidateOptionsResult.Skip;
    }
}
