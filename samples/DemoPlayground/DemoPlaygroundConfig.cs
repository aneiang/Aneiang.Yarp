namespace DemoPlayground;

/// <summary>
/// 绑定配置的强类型投影，字段与 <c>plugin.json</c> 里的 schema 一一对应。
/// </summary>
/// <remarks>
/// 每个字段都有安全的默认值：配置缺字段、旧版本残留字段、甚至是空对象，
/// 反序列化出来都还是一份「什么都不做」的配置，不会把路由打挂。
/// </remarks>
public sealed class DemoPlaygroundConfig
{
    /// <summary>总开关。关掉后绑定仍然保留，只是不再产生任何效果。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>工作模式：<c>stamp</c> / <c>echo</c> / <c>silent</c>。</summary>
    public string Mode { get; set; } = DemoPlaygroundMiddleware.ModeStamp;

    /// <summary>写进 <c>X-Demo-Badge</c> 的标记，用来确认命中的是哪个绑定。</summary>
    public string Badge { get; set; } = "demo";

    /// <summary>生效比例：0 表示每个请求都生效，1-100 表示只对这一比例的请求生效。</summary>
    public int SamplePercent { get; set; }

    /// <summary>转发前人为增加的延时（毫秒）。</summary>
    public int DelayMs { get; set; }

    /// <summary>请求路径以这些前缀开头时跳过。</summary>
    public List<string> SkipPaths { get; set; } = [];

    /// <summary>额外附加的响应头。</summary>
    public List<HeaderEntry> ResponseHeaders { get; set; } = [];

    /// <summary>echo 模式的返回细节。</summary>
    public EchoOptions Echo { get; set; } = new();
}

/// <summary>一条附加响应头。</summary>
public sealed class HeaderEntry
{
    /// <summary>头名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>头内容。</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>echo 模式的返回细节。</summary>
public sealed class EchoOptions
{
    /// <summary>返回的状态码。</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>是否缩进输出。</summary>
    public bool Pretty { get; set; } = true;
}
