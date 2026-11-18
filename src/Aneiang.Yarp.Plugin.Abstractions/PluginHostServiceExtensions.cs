using Microsoft.Extensions.DependencyInjection;

namespace Aneiang.Yarp.Plugins;

/// <summary>
/// Host-side helper that lets plugins register services into the host container.
/// </summary>
public static class PluginHostServiceExtensions
{
    /// <summary>
    /// Instantiates every plugin type that implements <see cref="IGatewayPluginHostServices"/> and
    /// lets it add services to <paramref name="services"/>.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="pluginTypes">Candidate plugin implementation types. Non-plugin types are ignored.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// A contributor has no parameterless constructor. Host-scoped contributions are applied while
    /// the collection is still being built, so there is no provider to inject from; silently
    /// skipping the plugin would leave its load-balancing policy missing at runtime, which is worse
    /// than failing at startup.
    /// </exception>
    public static IServiceCollection AddPluginHostServices(
        this IServiceCollection services,
        IEnumerable<Type> pluginTypes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pluginTypes);

        foreach (var type in pluginTypes.Distinct())
        {
            if (type is null || !typeof(IGatewayPluginHostServices).IsAssignableFrom(type))
                continue;

            if (type.IsAbstract || type.IsInterface)
                continue;

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new InvalidOperationException(
                    $"{type.FullName} implements {nameof(IGatewayPluginHostServices)} but has no parameterless constructor. " +
                    "Host-scoped plugin services are registered before the host container exists, so they cannot use constructor injection.");
            }

            var contributor = (IGatewayPluginHostServices)Activator.CreateInstance(type)!;
            contributor.ConfigureHostServices(services);
        }

        return services;
    }
}
