using Aneiang.Yarp.Dashboard.Infrastructure.Policies;

namespace Aneiang.Yarp.UnitTests;

/// <summary>
/// Catalog stand-in that answers by table instead of touching ASP.NET Core providers.
/// Anything not explicitly registered here resolves as <see cref="PolicyResolutionStatus.Found"/>,
/// so a test only has to state the exceptions it cares about.
/// </summary>
internal sealed class FakePolicyCatalog : IGatewayPolicyCatalog
{
    private readonly Dictionary<string, PolicyResolutionStatus> _answers = new(StringComparer.Ordinal);

    public List<string> Queried { get; } = new();

    public FakePolicyCatalog Answer(GatewayPolicyKind kind, string name, PolicyResolutionStatus status)
    {
        _answers[$"{kind}:{name}"] = status;
        return this;
    }

    public IReadOnlyList<GatewayPolicyInfo> GetPolicies(GatewayPolicyKind kind)
        => GatewayPolicyDefaults.All.Where(p => p.Kind == kind).ToList();

    public IReadOnlyList<GatewayPolicyInfo> GetAll() => GatewayPolicyDefaults.All;

    public Task<PolicyResolution> ResolveAsync(GatewayPolicyKind kind, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(new PolicyResolution(PolicyResolutionStatus.Found, null));

        var key = $"{kind}:{name}";
        Queried.Add(key);

        var status = _answers.TryGetValue(key, out var configured)
            ? configured
            : PolicyResolutionStatus.Found;

        return Task.FromResult(new PolicyResolution(status, name));
    }
}
