# 插件兼容矩阵（2.3.0.40）

这份文档回答一个问题：**我按 2.3.0.40 的契约写的插件，能不能跑在某个宿主上。**

三句话版本：

1. **契约就是 `Aneiang.Yarp.Plugin.Abstractions` 这个包。** 宿主版本与它同号 —— `2.3.0.40` 的宿主
   带的是 `Aneiang.Yarp.Plugin.Abstractions` `2.3.0.40`。
2. **插件不随包分发契约程序集**（`ExcludeAssets="runtime"`），宿主始终用自己的那一份。所以兼容性
   完全由插件 csproj 里那条包引用的版本决定，不会有"两个 `IGatewayPlugin` 类型"这种事。
3. **兼容不等于能加载。** 加载时还会校验 `plugin.json`（见下表），任何一项不满足都会在启用时报错，
   而不是在构建时 —— 清单是数据，构建过程看不到它。

装进宿主有两条路，效果相同：把发布目录拷进宿主的 `plugins/<id>/`，或者打开面板
`<prefix>/plugin-market` →「安装外部插件」→ 填发布目录的绝对路径。后者只做复制与登记，
**不重启网关、也不自动启用插件**（启用与绑定在「插件配置」里），所以"装上去"和"开始改流量"是两个动作。

## 内置插件矩阵

`order` 是代理管线的执行顺序（升序）。中间件在管线里从外到内按这个顺序排列，`fault-injection`
排在最后，紧挨转发器 —— 注入的故障对重试、熔断、指标来说必须和真实上游故障不可区分。

| 插件 | 名称 | 版本 | 作用域 | 能力 | order | schema | 依赖 |
|:--|:--|:--|:--|:--|:--:|:--:|:--|
| `service-discovery` | Service Discovery | 1.0 | Cluster | BackgroundService, Dashboard | 50 | v1 | — |
| `waf` | Web Application Firewall | 2.0 | Route | RequestMiddleware, Dashboard | 100 | v1 | — |
| `rate-limit` | Rate Limiting | 1.0 | Route | ProxyPipeline | 200 | v1 | — |
| `circuit-breaker` | Circuit Breaker | 1.0 | Cluster | ProxyPipeline | 300 | v1 | — |
| `compression` | Response Compression | 1.0 | Route | ProxyPipeline | 350 | v1 | — |
| `request-retry` | Request Retry | 1.0 | Route | ProxyPipeline | 400 | v1 | — |
| `response-cache` | Response Cache | 1.0 | Route | ProxyPipeline | 450 | v1 | — |
| `proxy-log` | Proxy Log | 1.0 | Route | RequestMiddleware, Dashboard, BackgroundService | 500 | v1 | — |
| `traffic-metrics` | Traffic Metrics | 1.0 | Route | ProxyPipeline, Dashboard | 600 | v1 | — |
| `cluster-metrics` | Cluster Metrics | 1.0 | Cluster | ProxyPipeline, Dashboard | 610 | v1 | — |
| `fault-injection` | Fault Injection | 1.0 | Route | ProxyPipeline | 640 | v1 | — |

> 每个内置插件的配置 schema 都是 `v1`，都以内联 JSON Schema 的形式随 `PluginSchemaReference` 发出，
> 面板的绑定编辑器渲染的就是它。改结构要发新的 `version` 并实现 `IPluginConfigurationMigrator`；
> 原地改 v1 会让已存的绑定在下次保存时莫名其妙地失败。

## `plugin.json` 的校验表

外部插件被启用时按顺序检查下面这些。**注意这些都是运行时检查**，所以"构建成功"不代表"能装上"。

| 字段 | 规则 | 不满足 |
|:--|:--|:--|
| `id` | 非空、全局唯一；与插件实例的 `PluginId` 必须一致 | `InvalidManifest` / `LoadFailed`（错误信息写明两个 id） |
| `name` | 非空 | `InvalidManifest` |
| `version` | 语义化版本（`1`、`1.0`、`1.0.0`、`1.0.0-beta.1` 均可） | `InvalidManifest` |
| `entryAssembly` | 必填、相对 `plugin.json`、且不能跳出插件目录 | `InvalidManifest` / `LoadFailed` |
| `entryType` | 必填，加载后必须实现 `IGatewayPlugin`，且有无参构造函数 | `LoadFailed` |
| `dependencies` | 每个依赖必须已安装，且满足版本约束（`>=2.0`、`<3.0` 这类比较符） | `DependencyUnsatisfied` |

## 外部插件能做什么、不能做什么

这块最容易踩，因为它不在类型签名里：

| 能力 | 外部插件 | 说明 |
|:--|:--:|:--|
| `ConfigureProxyPipeline` | ✅ | 2.3.0.40 起按 manifest `order` 参与代理管线。**之前不行** —— 管线顺序来自宿主里写死的 8 个内置 id，不在其中的插件配好了、绑好了、面板上也亮着，但永远不被调用 |
| `ConfigureServices` | ✅ | 在插件启用时针对独立的服务集合构建 |
| `ConfigureDashboard` | ✅ | 贡献导航项与卡片需要 `PluginCapability.Dashboard` |
| `ConfigureMiddleware` | ❌ | 应用级管线在启动时构建完成，之后无法再往里加中间件。宿主只对内置 `waf` 调用它。写在里面的中间件是**静默空操作** |
| `IGatewayPluginHostServices` | ✅ | 需要 `LoadBalancingPolicy` / `Transform` 能力时**必须**走这里，写进 `ConfigureServices` 会静默不生效 |
| 卸载 | ✅ | 每个插件装载在自己的可回收 `AssemblyLoadContext` 里；禁用后卸载，面板会把"等待回收 / 已回收"如实显示出来 |

## 版本演进记录

| 版本 | 契约变化 | 对外部插件的影响 |
|:--|:--|:--|
| 2.3.0.40 | 代理管线改为按 `manifest.order` 排序（原先按宿主内写死的 id 列表） | **外部插件的中间件从"静默不执行"变为"执行"**。若某个插件依赖"不会被调用"这一事实，需要重新审视 |
| 2.3.0.40 | `IGatewayPlugin.ConfigureMiddleware` 的文档改为如实描述（仅内置 WAF 生效） | 无行为变化，只是不再误导 |
| 2.3.0.40 | 新增 `PluginDependency` / `PluginCapability.LoadBalancingPolicy` / `PluginCapability.Transform` | 旧插件无需改动，默认清单仍可加载 |

## 自测

契约测试跑在仓库的单元测试里，覆盖全部内置插件：

```bash
dotnet test tests/Aneiang.Yarp.UnitTests --filter "FullyQualifiedName~PluginContractTests"
```

它钉住的是**能被破坏而不报错的那些不变量**：id 唯一且格式合法、清单与实例的 id/名称/版本一致、
版本是语义化版本、`order` 互不重复（重复会让管线顺序取决于字典遍历顺序）、schema 是合法 JSON、
声明了 `LoadBalancingPolicy` / `Transform` 的插件必须实现 `IGatewayPluginHostServices`、依赖都能在
已装插件里解析、以及本文件里的矩阵与代码里的插件集合完全一致（多一个少一个都会红）。

外部插件自己也可以照抄这套断言 —— 它不依赖宿主内部类型，只依赖 `Aneiang.Yarp.Plugin.Abstractions`。
