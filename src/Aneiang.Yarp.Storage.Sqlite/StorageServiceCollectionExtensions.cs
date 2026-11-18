using Aneiang.Yarp.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Aneiang.Yarp.Storage.Sqlite;

/// <summary>
/// Extension methods for registering SQLite storage services.
/// </summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Register SQLite storage repositories based on <c>Gateway:Storage</c> configuration section.
    /// </summary>
    public static IServiceCollection AddAneiangStorage(this IServiceCollection services)
    {
        services.AddOptions<StorageOptions>()
            .BindConfiguration(StorageOptions.SectionName);

        // Fallback environment resolver so storage works on its own (tests, tools, hosts that only
        // pull in the storage package). The gateway core registers the real one, which additionally
        // lets the dashboard switch the editing view at runtime.
        services.TryAddSingleton<IStorageEnvironmentResolver>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            return new SingleEnvironmentResolver(options.Environments.Runtime);
        });

        // Shared connection factory. Schema migration is triggered lazily per environment
        // on first connection use (via Lazy<Task> in SqliteConnectionFactory), eliminating the
        // startup race condition that existed when migrator was a parallel IHostedService.
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<IDbConnectionFactory>(sp => sp.GetRequiredService<SqliteConnectionFactory>());

        // Individual repositories
        services.AddSingleton<IRouteRepository, SqliteRouteRepository>();
        services.AddSingleton<IClusterRepository, SqliteClusterRepository>();
        services.AddSingleton<IConfigHistoryRepository, SqliteConfigHistoryRepository>();
        services.AddSingleton<IAuditLogRepository, SqliteAuditLogRepository>();
        services.AddSingleton<IProxyLogRepository, SqliteProxyLogRepository>();
        services.AddSingleton<IPluginConfigurationRepository, SqlitePluginConfigurationRepository>();
        services.AddSingleton<IGatewayPluginRepository, SqliteGatewayPluginRepository>();
        services.AddSingleton<IPluginRuntimeStateRepository, SqlitePluginRuntimeStateRepository>();
        services.AddSingleton<ILogSettingsRepository, SqliteLogSettingsRepository>();
        services.AddSingleton<IWebhookSettingsRepository, SqliteWebhookSettingsRepository>();
        services.AddSingleton<IWebhookDeliveryRecordRepository, SqliteWebhookDeliveryRecordRepository>();

        // AI repositories (always registered; tables created by migration)
        services.AddSingleton<IAIConversationRepository, SqliteAIConversationRepository>();
        services.AddSingleton<IAIAnalysisRepository, SqliteAIAnalysisRepository>();

        return services;
    }
}
