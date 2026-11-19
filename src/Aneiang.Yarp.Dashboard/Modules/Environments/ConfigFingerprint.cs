using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aneiang.Yarp.Models;

namespace Aneiang.Yarp.Dashboard.Modules.Environments;

/// <summary>
/// Stable fingerprint of a pair of environments, used to bind an approval to the exact state that
/// was reviewed.
/// </summary>
/// <remarks>
/// <para>
/// Hashing the <b>inputs</b> rather than the rendered diff is deliberate. The first version hashed
/// the diff, and it missed a whole class of change: a route that is "added" carries no field-level
/// detail, so editing that route between approval and apply produced an identical diff — and the
/// promotion went ahead with content nobody had seen. Hashing what would be written closes that,
/// and also covers fields the human-readable diff leaves out on purpose.
/// </para>
/// <para>
/// Consequence worth knowing: a change on the target side that the diff cannot show (metadata, a
/// timeout) also invalidates the approval. That is the correct trade — the alternative is applying
/// something that was never reviewed.
/// </para>
/// </remarks>
public static class ConfigFingerprint
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Of(GatewayDynamicConfig source, GatewayDynamicConfig target)
    {
        var builder = new StringBuilder();

        Append(builder, "source", source);
        Append(builder, "target", target);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Append(StringBuilder builder, string label, GatewayDynamicConfig config)
    {
        builder.Append("## ").Append(label).Append('\n');

        foreach (var route in config.Routes.OrderBy(r => r.Config.RouteId, StringComparer.Ordinal))
            builder.Append("R:").Append(JsonSerializer.Serialize(route.Config, Json)).Append('\n');

        foreach (var cluster in config.Clusters.OrderBy(c => c.Config.ClusterId, StringComparer.Ordinal))
            builder.Append("C:").Append(JsonSerializer.Serialize(cluster.Config, Json)).Append('\n');
    }
}
