namespace Aneiang.Yarp.Plugin.Waf;

/// <summary>What a rule does when it fires.</summary>
public enum WafAction
{
    /// <summary>Reject the request.</summary>
    Block,

    /// <summary>Stop evaluating the pipeline and forward the request.</summary>
    Allow,

    /// <summary>Record a hit and keep going — the request is forwarded untouched.</summary>
    Log,

    /// <summary>Stamp a tag onto the request for the upstream to act on, and keep going.</summary>
    Tag,

    /// <summary>
    /// Consume one unit of the rule's quota and reject once it is exhausted. Unlike
    /// <see cref="Block"/> this is not a verdict about the request itself — the same request is
    /// fine the first five times and abusive the sixth.
    /// </summary>
    RateLimit
}

/// <summary>What a rate-limit quota is counted per.</summary>
public enum WafPartition
{
    /// <summary>Per client address. The default, and the only one that is meaningful without a path.</summary>
    Ip,

    /// <summary>Per request path, shared by every client. Rarely what you want on a public route.</summary>
    Path,

    /// <summary>Per client address per path — stops a spray across endpoints without capping one endpoint.</summary>
    IpPath,

    /// <summary>One bucket for the whole route. Use for a global safety valve, not for abuse control.</summary>
    Global
}

/// <summary>Which part of the request a condition reads.</summary>
public enum WafSource
{
    Method,
    Path,
    Query,
    Body,
    Header,
    Cookie,
    UserAgent,
    Ip,
    ContentType,
    BodySize
}

/// <summary>How a condition compares.</summary>
public enum WafOperator
{
    Equals,
    Contains,
    Prefix,
    Suffix,
    Regex,
    Cidr,
    GreaterThan,
    LessThan
}

/// <summary>Detectors that ship with the plugin.</summary>
public enum WafBuiltin
{
    None,
    SqlInjection,
    Xss,
    PathTraversal
}
