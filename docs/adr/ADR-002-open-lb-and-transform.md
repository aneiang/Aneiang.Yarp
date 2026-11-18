# ADR-2：插件模型开放负载均衡与 Transform 扩展点

- **状态**：已接受，**2.3.0.40 已实施**（实施方式与决策稿有出入，见文末「实施记录」）
- **决策者**：架构
- **影响范围**：`Aneiang.Yarp.Plugin.Abstractions` 的 `PluginCapability`、插件加载器、v2.5 的权重灰度

## 背景

当前插件能挂的位置只有四个（`PluginCapability`）：

```csharp
public enum PluginCapability
{
    RequestMiddleware,   // 中间件：能看能改请求，但改不了"选哪个目标"
    ProxyPipeline,       // 代理管道：能包住整个转发过程
    Dashboard,           // UI 扩展
    BackgroundService    // 后台任务
}
```

这决定了插件的天花板：**它能在请求到达之后做事，不能在"选目标"这一步做事**。

具体丢失的能力：

- 按权重把流量分到新旧两个版本 → 需要 `ILoadBalancingPolicy`
- 灰度时改写上游路径 / 添加标识头 → 需要 `ITransformProvider`
- 按请求特征（用户、地域、租户）选目标 → 同样需要 LB Policy

这不是"缺一个功能"，而是**产品主线「流量治理」不成立**：没有这两类扩展点，任何灰度、A/B、金丝雀都只能靠改集群配置硬切，做不到按比例放量。

## 关键事实（决定了工作量）

框架**自己已经在 DI 里用了这两个扩展点**：

| 扩展点 | 现有实现 | 注册位置 |
|:--|:--|:--|
| `ILoadBalancingPolicy` | `IpBasedLoadBalancingPolicy` | `AneiangYarpServiceCollectionExtensions.cs` |
| `ITransformProvider` | `DownstreamCaptureTransformProvider` | `DashboardServiceCollectionExtensions.cs` |

也就是说扩展点本身是通的、YARP 侧不需要任何改动。缺的只是两件事：

1. `PluginCapability` 没有对应的枚举值，插件无法声明"我要注册一个 LB 策略"，加载器也不会去调用它；
2. 插件拿不到 `IServiceCollection`——它只能在运行时被调用，不能在**容器构建期**注册服务。

## 决策

**开放，且只做最小改动：**

1. `PluginCapability` 增加 `LoadBalancingPolicy` 与 `Transform` 两个值。
2. 插件接口增加一个容器构建期入口（形如 `void ConfigureServices(IServiceCollection services, PluginContext context)`），由加载器在 `AddAneiangYarp` 之后、容器构建之前调用。
3. 插件仍**不允许**替换 `IProxyConfigProvider`、`IForwarderHttpClientFactory`、路由匹配逻辑——这些是内核不变量，误用会让排障失去参照系。

## 后果

**正面**

- 权重灰度插件可以做成纯插件，不碰内核：注册一个 `WeightedRoundRobinPolicy` + 一个 `ITransformProvider`。
- 与 ADR-3 呼应：限流保留扩展位、LB 开放扩展点，流量治理的两条腿都在插件侧，内核保持薄。
- 工作量从"改内核"降为"加枚举 + 加一个注册钩子"（路线图原估 5d，实际约 2d）。

**负面**

- 插件的启动顺序变得重要：LB 策略必须在 `MapReverseProxy` 之前注册，否则 YARP 启动时解析不到策略名会抛异常。加载器需要显式的"容器期 / 运行期"两阶段，并在文档里写清楚。
- 插件的故障面变大：一个写错的 LB 策略会直接影响全站转发，而中间件写错只影响单个路由。插件启用前需要更强的校验（复用 v2.4 的策略目录与预检）。

## 替代方案为何未采纳

- **维持现状**：等于承认"流量治理"这个主线不做，v2.5 之后的产品故事只剩可观测性补丁。
- **开放全部 YARP 扩展点**：包括 `IForwarderHttpClientFactory` 与控制平面 provider。这会让"为什么这个请求没走我预期的那条链路"变成不可回答的问题，排障成本远大于收益。

## 复审条件

- 若插件启用后出现"策略名解析失败导致启动失败"的线上问题 → 收紧为"只允许注册，不允许覆盖"，并强制插件声明策略名前缀。

---

## 实施记录（2.3.0.40）

### 决策第 2 条被推翻：`ConfigureServices` 不能承担这个职责

决策稿假设"插件接口增加的容器构建期入口"可以复用现有的 `IGatewayPlugin.ConfigureServices`。实施时核实代码否定了这个假设：

```
PluginRuntimeDomain.PrepareAsync(enabledPluginIds)
  ├─ var services = new ServiceCollection();      ← 新建的，不是宿主那个
  ├─ plugin.ConfigureServices(services);          ← 在这里注册
  └─ services.BuildServiceProvider()              ← 插件自己的容器
```

宿主容器在 `AddAneiangYarp*` 返回时就已经定型，YARP 的 `LoadBalancingPolicy` 单例在首次解析时把 `IEnumerable<ILoadBalancingPolicy>` 拍成字典。因此**在 `ConfigureServices` 里注册 LB 策略永远不会被看见** —— 不是"可能不生效"，是确定不生效。

### 实际做法

新增独立接口，而不是改动既有签名（既有 10 个插件因此一行都不用改）：

```csharp
// Aneiang.Yarp.Plugin.Abstractions
public interface IGatewayPluginHostServices
{
    string PluginId { get; }
    void ConfigureHostServices(IServiceCollection services);
}

public static class PluginHostServiceExtensions
{
    public static IServiceCollection AddPluginHostServices(
        this IServiceCollection services, IEnumerable<Type> pluginTypes);
}
```

`AddAneiangYarpDashboard` 在最后一步扫描 `IGatewayPlugin` 描述符，对实现了该接口的类型**实例化一次**并调用。

三条硬约束，都写进了抛错信息：

1. **必须有无参构造函数**。此时没有 `IServiceProvider`，构造注入不可用。缺失则启动即抛 `InvalidOperationException` —— 静默跳过会让集群指向一个不存在的策略，问题只在运行时暴露。
2. **只允许注册，不允许替换**。API 只暴露 `IServiceCollection`，没有"移除已有注册"的入口，决策第 3 条（不碰内核不变量）天然成立。
3. **执行时机固定在容器构建前**。决策稿担心的"插件启动顺序"因此消失：所有贡献都在同一个阶段完成，顺序只影响注册先后，不影响可见性。

### 副作用

`PluginCapability` 的两个新值不是"声明即可用"：声明了但没有通过 `IGatewayPluginHostServices` 真正注册，得到的仍是一个解析不到的策略名。因此校验侧把"策略名是否存在"作为唯一判据（`ConfigurationValidator.KnownLoadBalancingPolicies` + 自定义策略的 Warning），而不是信任 manifest 的声明。

