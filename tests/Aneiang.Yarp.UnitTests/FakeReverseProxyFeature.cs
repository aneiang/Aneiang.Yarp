using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// The minimum <see cref="IReverseProxyFeature"/> a proxy-pipeline middleware reads a route id from.
/// </summary>
/// <remarks>
/// YARP builds this feature inside the forwarder pipeline, so middleware tests have to supply one.
/// Only <see cref="Route"/> is meaningful here; destination state is left empty unless a test needs
/// it, which keeps the "no destination was ever proxied" case (a short-circuited request) the default.
/// </remarks>
internal sealed class FakeReverseProxyFeature : IReverseProxyFeature
{
    private const string ClusterId = "c1";

    private static readonly ClusterState State = new(ClusterId);

    public FakeReverseProxyFeature(string routeId)
    {
        Route = new RouteModel(
            new RouteConfig
            {
                RouteId = routeId,
                ClusterId = ClusterId,
                Match = new RouteMatch { Path = "/{**catchAll}" }
            },
            State,
            HttpTransformer.Default);

        Cluster = new ClusterModel(
            new ClusterConfig { ClusterId = ClusterId },
            new HttpMessageInvoker(new SocketsHttpHandler()));
    }

    public RouteModel Route { get; }

    public ClusterModel Cluster { get; }

    public IReadOnlyList<DestinationState> AllDestinations { get; } = [];

    public IReadOnlyList<DestinationState> AvailableDestinations { get; set; } = [];

    public DestinationState? ProxiedDestination { get; set; }
}
