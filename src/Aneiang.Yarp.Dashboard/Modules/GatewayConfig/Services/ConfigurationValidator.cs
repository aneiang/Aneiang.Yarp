using Aneiang.Yarp.Dashboard.Infrastructure.Policies;
using Aneiang.Yarp.Services;
using Yarp.ReverseProxy.Configuration;

namespace Aneiang.Yarp.Dashboard.Modules.GatewayConfig.Services;

/// <summary>How badly an issue blocks the save.</summary>
public enum ValidationSeverity
{
    /// <summary>The write is refused — the config would break at runtime.</summary>
    Error,

    /// <summary>Saved, but the operator should know.</summary>
    Warning
}

/// <summary>One finding, addressed to a form field so the UI can point at it.</summary>
public sealed record ConfigValidationIssue(
    string Severity,
    string Field,
    string Message,
    string? Hint = null);

/// <summary>Outcome of a pre-flight check.</summary>
public sealed record ConfigValidationReport(
    bool Ok,
    IReadOnlyList<ConfigValidationIssue> Errors,
    IReadOnlyList<ConfigValidationIssue> Warnings)
{
    public static ConfigValidationReport From(IEnumerable<ConfigValidationIssue> issues)
    {
        var list = issues.ToList();
        return new ConfigValidationReport(
            list.All(i => i.Severity != nameof(ValidationSeverity.Error)),
            list.Where(i => i.Severity == nameof(ValidationSeverity.Error)).ToList(),
            list.Where(i => i.Severity == nameof(ValidationSeverity.Warning)).ToList());
    }
}

/// <summary>
/// Pre-flight validation for route and cluster configuration.
///
/// The point is to move failures from request time to save time. YARP itself only
/// complains when it builds the endpoint — by then the config is already live and a
/// 500 is in front of a real user. Everything checked here is something YARP would
/// eventually reject, or something that would silently do nothing.
/// </summary>
public interface IConfigurationValidator
{
    /// <summary>
    /// Validates a route against the current configuration.
    /// </summary>
    /// <param name="route">Route to check.</param>
    /// <param name="clusterWillBeCreated">
    /// True when the payload also creates the referenced cluster, so "unknown cluster"
    /// must not be reported.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConfigValidationReport> ValidateRouteAsync(
        RouteConfig route, bool clusterWillBeCreated, CancellationToken ct = default);

    /// <summary>Validates a cluster in isolation.</summary>
    Task<ConfigValidationReport> ValidateClusterAsync(ClusterConfig cluster, CancellationToken ct = default);

    /// <summary>
    /// Cross-checks the whole configuration: routes pointing at nothing, clusters with no
    /// targets, duplicate match rules. This is the "体检" that catches drift that built up
    /// over many individual edits.
    /// </summary>
    Task<ConfigValidationReport> ValidateAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Cross-checks a proposed configuration as one unit — the shape used by import and
    /// rollback, where applying half a batch is worse than refusing all of it.
    ///
    /// Routes are resolved against the proposed clusters *plus* the ones already in effect,
    /// so a batch that creates a cluster and the routes pointing at it is not reported as
    /// broken. Fields are prefixed with the owning entity (<c>Clusters.c1.Destinations…</c>)
    /// because a flat list of paths is useless when 30 clusters fail at once.
    /// </summary>
    /// <param name="routes">Proposed routes.</param>
    /// <param name="clusters">Proposed clusters.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConfigValidationReport> ValidateBatchAsync(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ConfigurationValidator : IConfigurationValidator
{
    private static readonly string[] KnownLoadBalancingPolicies =
        { "PowerOfTwoChoices", "FirstAlphabetical", "Random", "RoundRobin", "LeastRequests", "Weighted", "IpBased" };

    private static readonly string[] KnownSessionAffinityPolicies =
        { "HashCookie", "ArrCookie", "Cookie", "CustomHeader" };



    private readonly IDynamicYarpConfigService _config;
    private readonly IGatewayPolicyCatalog _policies;

    public ConfigurationValidator(IDynamicYarpConfigService config, IGatewayPolicyCatalog policies)
    {
        _config = config;
        _policies = policies;
    }

    public async Task<ConfigValidationReport> ValidateRouteAsync(
        RouteConfig route, bool clusterWillBeCreated, CancellationToken ct = default)
    {
        var issues = new List<ConfigValidationIssue>();

        if (string.IsNullOrWhiteSpace(route.RouteId))
            issues.Add(Error("RouteId", "路由 ID 不能为空"));

        if (string.IsNullOrWhiteSpace(route.ClusterId))
            issues.Add(Error("ClusterId", "必须指定目标集群"));

        var match = route.Match;
        if (match == null || (string.IsNullOrWhiteSpace(match.Path) && (match.Hosts == null || match.Hosts.Count == 0)))
        {
            issues.Add(Error("Match", "必须设置 Path 或 Hosts，否则这条路由永远不会命中"));
        }

        if (match?.Methods is { Count: 0 })
            issues.Add(Error("Match.Methods", "Methods 已声明但为空，请求永远不会匹配"));

        if (match?.Headers != null)
        {
            for (var i = 0; i < match.Headers.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(match.Headers[i]?.Name))
                    issues.Add(Error($"Match.Headers[{i}].Name", "Header 匹配条件缺少 Name"));
            }
        }

        // YARP refuses to build an endpoint that sets both; catch it before the save.
        if (route.Timeout != null && !string.IsNullOrWhiteSpace(route.TimeoutPolicy))
            issues.Add(Error("TimeoutPolicy", "Timeout 与 TimeoutPolicy 只能设置一个"));

        if (route.Timeout is { } timeout && timeout <= TimeSpan.Zero)
            issues.Add(Error("Timeout", "超时必须大于 0"));

        if (!clusterWillBeCreated
            && !string.IsNullOrWhiteSpace(route.ClusterId)
            && _config.GetCluster(route.ClusterId!) == null)
        {
            issues.Add(Error("ClusterId",
                $"集群 '{route.ClusterId}' 不存在",
                "先创建该集群，并在表单里填写目标节点"));
        }

        issues.AddRange(await CheckPoliciesAsync(route, ct));

        var active = route.Metadata != null
                     && route.Metadata.TryGetValue("Enabled", out var enabledFlag)
                     && string.Equals(enabledFlag, "false", StringComparison.OrdinalIgnoreCase);

        if (active)
            issues.Add(Warning("Metadata.Enabled", "该路由当前处于禁用状态，保存后仍不会转发流量"));

        return ConfigValidationReport.From(issues);
    }

    public Task<ConfigValidationReport> ValidateClusterAsync(ClusterConfig cluster, CancellationToken ct = default)
    {
        var issues = new List<ConfigValidationIssue>();

        if (string.IsNullOrWhiteSpace(cluster.ClusterId))
            issues.Add(Error("ClusterId", "集群 ID 不能为空"));

        if (cluster.Destinations == null || cluster.Destinations.Count == 0)
        {
            issues.Add(Error("Destinations", "至少需要一个目标节点，否则该集群无法转发任何请求"));
        }
        else
        {
            foreach (var (name, destination) in cluster.Destinations)
            {
                var address = destination?.Address;
                if (string.IsNullOrWhiteSpace(address))
                {
                    issues.Add(Error($"Destinations.{name}.Address", "目标地址不能为空"));
                    continue;
                }

                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    issues.Add(Error($"Destinations.{name}.Address",
                        $"'{address}' 不是合法的 http/https 地址",
                        "地址必须带协议，例如 http://10.0.0.5:8080"));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(cluster.LoadBalancingPolicy)
            && !KnownLoadBalancingPolicies.Contains(cluster.LoadBalancingPolicy, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(Warning("LoadBalancingPolicy",
                $"'{cluster.LoadBalancingPolicy}' 不是内置策略",
                "自定义负载均衡策略需要在宿主代码里注册，否则启动时会解析失败"));
        }

        issues.AddRange(CheckWeights(cluster));

        var affinity = cluster.SessionAffinity;
        if (affinity is { Enabled: true })
        {
            if (string.IsNullOrWhiteSpace(affinity.AffinityKeyName))
                issues.Add(Error("SessionAffinity.AffinityKeyName",
                    "启用会话亲和后必须指定 AffinityKeyName（cookie 或 header 名）"));

            if (!string.IsNullOrWhiteSpace(affinity.Policy)
                && !KnownSessionAffinityPolicies.Contains(affinity.Policy, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(Error("SessionAffinity.Policy", $"未知的会话亲和策略 '{affinity.Policy}'"));
            }
        }

        var healthCheck = cluster.HealthCheck;
        if (healthCheck?.Active is { Enabled: true } activeHealth)
        {
            if (activeHealth.Interval is { } interval && interval <= TimeSpan.Zero)
                issues.Add(Error("HealthCheck.Active.Interval", "探测间隔必须大于 0"));

            if (activeHealth.Timeout is { } probeTimeout && probeTimeout <= TimeSpan.Zero)
                issues.Add(Error("HealthCheck.Active.Timeout", "探测超时必须大于 0"));
        }

        if (cluster.HttpClient?.DangerousAcceptAnyServerCertificate == true)
        {
            issues.Add(Warning("HttpClient.DangerousAcceptAnyServerCertificate",
                "已关闭上游证书校验",
                "仅建议在自签名测试环境使用，生产环境请改用受信任证书"));
        }

        var version = cluster.HttpRequest?.Version;
        if (version != null && (version.Major < 1 || version.Major > 3))
            issues.Add(Error("HttpRequest.Version", $"不支持的 HTTP 版本 '{version}'"));

        return Task.FromResult(ConfigValidationReport.From(issues));
    }

    /// <summary>
    /// Validates the weight table used by the <c>Weighted</c> policy.
    /// </summary>
    /// <remarks>
    /// A weight table fails quietly in three ways, and all three need to be caught at save time:
    /// a non-numeric weight silently falls back to the default; every weight set to 0 silently
    /// collapses all traffic onto the first destination; and a rollout tag on a cluster that is not
    /// using <c>Weighted</c> does nothing at all. The second one is the dangerous case — the gateway
    /// keeps serving, so nothing looks broken until someone reads the access log.
    /// </remarks>
    private static IEnumerable<ConfigValidationIssue> CheckWeights(ClusterConfig cluster)
    {
        if (cluster.Destinations is not { Count: > 0 })
            yield break;

        var weightKey = ReadMetadataValue(cluster.Metadata, WeightedLoadBalancingPolicy.MetadataWeightKey)
                        ?? WeightedLoadBalancingPolicy.DefaultWeightKey;
        var tagKey = ReadMetadataValue(cluster.Metadata, WeightedLoadBalancingPolicy.MetadataTagKey)
                     ?? WeightedLoadBalancingPolicy.DefaultTagKey;

        var isWeighted = string.Equals(
            cluster.LoadBalancingPolicy,
            WeightedLoadBalancingPolicy.PolicyName,
            StringComparison.OrdinalIgnoreCase);

        var receivingTraffic = 0;
        var taggedDestinations = 0;

        foreach (var (name, destination) in cluster.Destinations)
        {
            var metadata = destination?.Metadata;

            if (metadata is not null && metadata.TryGetValue(tagKey, out var tag) && !string.IsNullOrWhiteSpace(tag))
                taggedDestinations++;

            var raw = metadata is not null && metadata.TryGetValue(weightKey, out var value) ? value : null;
            if (string.IsNullOrWhiteSpace(raw))
            {
                // No weight on the node at all: the policy applies Weighted.DefaultWeight (1).
                receivingTraffic++;
                continue;
            }

            if (!int.TryParse(raw.Trim(), out var weight) || weight < 0)
            {
                yield return Error($"Destinations.{name}.Metadata.{weightKey}",
                    $"权重 '{raw}' 不是非负整数",
                    "权重是整数，例如 90；0 表示暂时不接收流量");
                continue;
            }

            if (weight > 0)
                receivingTraffic++;
        }

        if (isWeighted && receivingTraffic == 0)
        {
            yield return Error("Destinations",
                "所有目标节点的权重都是 0，Weighted 策略会把全部流量压到第一个节点",
                "至少给一个节点设置大于 0 的权重");
        }

        if (!isWeighted && taggedDestinations > 0)
        {
            yield return Warning("Destinations",
                $"节点带有 '{tagKey}' 标记，但负载策略不是 Weighted，灰度标记不会生效",
                $"把 LoadBalancingPolicy 改为 {WeightedLoadBalancingPolicy.PolicyName} 才会按标记分流");
        }
    }

    private static string? ReadMetadataValue(IReadOnlyDictionary<string, string>? metadata, string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    public async Task<ConfigValidationReport> ValidateAllAsync(CancellationToken ct = default)
    {
        var issues = new List<ConfigValidationIssue>();

        var routes = _config.GetRoutes();
        var clusters = _config.GetClusters();
        var clusterIds = clusters
            .Select(c => c.ClusterId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in routes)
        {
            if (!string.IsNullOrWhiteSpace(route.ClusterId))
            {
                referenced.Add(route.ClusterId!);
                if (!clusterIds.Contains(route.ClusterId!))
                {
                    issues.Add(Error($"Routes.{route.RouteId}.ClusterId",
                        $"路由 '{route.RouteId}' 指向不存在的集群 '{route.ClusterId}'",
                        "这条路由会直接返回 503，请补建集群或改指向"));
                }
            }

            var routeReport = await ValidateRouteAsync(route, clusterWillBeCreated: true, ct);
            issues.AddRange(routeReport.Errors.Where(e => !e.Field.StartsWith("ClusterId", StringComparison.Ordinal)));
        }

        foreach (var cluster in clusters)
        {
            if (cluster.Destinations == null || cluster.Destinations.Count == 0)
            {
                issues.Add(Error($"Clusters.{cluster.ClusterId}.Destinations",
                    $"集群 '{cluster.ClusterId}' 没有任何目标节点"));
            }

            var clusterReport = await ValidateClusterAsync(cluster, ct);
            // Skip the "no destinations" duplicate: already reported above with better wording.
            issues.AddRange(clusterReport.Errors.Where(e => e.Field != "Destinations"));
            issues.AddRange(clusterReport.Warnings);

            if (!string.IsNullOrWhiteSpace(cluster.ClusterId) && !referenced.Contains(cluster.ClusterId!))
            {
                issues.Add(Warning($"Clusters.{cluster.ClusterId}",
                    $"集群 '{cluster.ClusterId}' 没有被任何路由引用"));
            }
        }

        // Two routes with the same match: the first one registered wins and the other is
        // silently dead. Worth flagging because the UI shows both as healthy.
        foreach (var group in routes
                     .Where(r => !string.IsNullOrWhiteSpace(r.Match?.Path))
                     .GroupBy(r => r.Match!.Path!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var ids = string.Join(", ", group.Select(r => r.RouteId));
            issues.Add(Warning($"Routes.{group.Key}.Match.Path",
                $"多个路由使用相同路径 {group.Key}：{ids}",
                "只有优先级最高的一条会命中，其余是死路由"));
        }

        return ConfigValidationReport.From(issues);
    }

    public async Task<ConfigValidationReport> ValidateBatchAsync(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters,
        CancellationToken ct = default)
    {
        var issues = new List<ConfigValidationIssue>();

        var proposedClusterIds = clusters
            .Select(c => c.ClusterId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var cluster in clusters)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = $"Clusters.{cluster.ClusterId}";
            var report = await ValidateClusterAsync(cluster, ct);
            issues.AddRange(report.Errors.Select(i => i with { Field = Prefix(prefix, i.Field) }));
            issues.AddRange(report.Warnings.Select(i => i with { Field = Prefix(prefix, i.Field) }));
        }

        foreach (var route in routes)
        {
            ct.ThrowIfCancellationRequested();
            var prefix = $"Routes.{route.RouteId}";

            // A cluster counts as existing when the same batch creates it, or when it is
            // already live (import merges into the current config rather than replacing it).
            var clusterWillExist =
                !string.IsNullOrWhiteSpace(route.ClusterId)
                && (proposedClusterIds.Contains(route.ClusterId!) || _config.GetCluster(route.ClusterId!) != null);

            var report = await ValidateRouteAsync(route, clusterWillExist, ct);
            issues.AddRange(report.Errors.Select(i => i with { Field = Prefix(prefix, i.Field) }));
            issues.AddRange(report.Warnings.Select(i => i with { Field = Prefix(prefix, i.Field) }));
        }

        // Duplicate match paths inside one batch: importing a snapshot exported from a
        // misconfigured gateway is exactly how these get propagated.
        foreach (var group in routes
                     .Where(r => !string.IsNullOrWhiteSpace(r.Match?.Path))
                     .GroupBy(r => r.Match!.Path!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(Warning($"Routes.{group.Key}.Match.Path",
                $"本批次有 {group.Count()} 条路由使用相同路径 {group.Key}：{string.Join(", ", group.Select(r => r.RouteId))}",
                "只有优先级最高的一条会命中，其余是死路由"));
        }

        return ConfigValidationReport.From(issues);
    }

    private static string Prefix(string prefix, string field)
        => string.IsNullOrEmpty(field) ? prefix : $"{prefix}.{field}";

    /// <summary>
    /// Policy references are the highest-value check here: a name that was never registered
    /// used to save fine and then fail at request time with no useful message.
    /// </summary>
    private async Task<IEnumerable<ConfigValidationIssue>> CheckPoliciesAsync(RouteConfig route, CancellationToken ct)
    {
        var issues = new List<ConfigValidationIssue>();

        var checks = new (GatewayPolicyKind Kind, string Field, string? Value)[]
        {
            (GatewayPolicyKind.Authorization, nameof(route.AuthorizationPolicy), route.AuthorizationPolicy),
            (GatewayPolicyKind.Cors, nameof(route.CorsPolicy), route.CorsPolicy),
            (GatewayPolicyKind.RateLimiter, nameof(route.RateLimiterPolicy), route.RateLimiterPolicy),
            (GatewayPolicyKind.OutputCache, nameof(route.OutputCachePolicy), route.OutputCachePolicy)
        };

        foreach (var (kind, field, value) in checks)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            ct.ThrowIfCancellationRequested();
            var resolution = await _policies.ResolveAsync(kind, value);

            switch (resolution.Status)
            {
                case PolicyResolutionStatus.Unknown:
                    issues.Add(Error(field,
                        $"策略 '{value}' 未注册，YARP 构建端点时会直接抛异常",
                        "从下拉里选内置策略（gw-*），或先在 Program.cs 中注册同名策略"));
                    break;

                case PolicyResolutionStatus.Unverifiable:
                    issues.Add(Warning(field,
                        $"无法确认策略 '{value}' 是否存在",
                        "ASP.NET Core 未公开该类策略的枚举接口；若未在宿主注册，运行时会报错"));
                    break;
            }
        }

        return issues;
    }

    private static ConfigValidationIssue Error(string field, string message, string? hint = null)
        => new(nameof(ValidationSeverity.Error), field, message, hint);

    private static ConfigValidationIssue Warning(string field, string message, string? hint = null)
        => new(nameof(ValidationSeverity.Warning), field, message, hint);
}
