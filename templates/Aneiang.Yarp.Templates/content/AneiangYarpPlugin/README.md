# AneiangYarpPlugin

An out-of-tree gateway plugin. It builds into a folder the host can pick up: your assembly, its
dependencies, and a generated `plugin.json`.

```bash
dotnet build -c Release
# → bin/Release/net8.0/plugin.json  (what the host reads)

dotnet publish -c Release -o out
# → out/plugin.json + everything it needs; copy this folder into the host's plugins directory
```

## 装进宿主

1. `dotnet publish -c Release -o out`
2. 把 `out/` 整个目录拷到宿主的插件目录（默认 `<网关内容根>/plugins/<插件 id>/`，可用
   `Gateway:Dashboard:PluginDirectory` 改）
3. 重启宿主 —— 或者在面板「插件中心」里启用它（外部插件可以被发现但不加载，这是刻意的：
   一个有问题的程序集不该让网关起不来）

宿主会做四件事，任何一条不满足都会在启用时报错（而不是在构建时）：

| 检查 | 不满足时的现象 |
|:--|:--|
| `plugin.json` 能反序列化、`id`/`name`/`version`/`entryAssembly`/`entryType` 都在 | 注册状态 `InvalidManifest`，错误信息写在面板上 |
| `version` 是语义化版本 | 同上 |
| `entryAssembly` 在插件目录内、文件存在 | `LoadFailed` |
| `entryType` 实现 `IGatewayPlugin`，且实例的 `PluginId` 与清单 `id` 一致 | `LoadFailed`，错误信息写明两个 id |

## `plugin.json` 从哪来

**它是生成的，不是手写的。** 构建时由 `plugin.manifest.json` 渲染而成，五个带 `$...$` 的字段来自
`.csproj` 里的属性：

| 占位符 | 来源 |
|:--|:--|
| `$PluginId$` | `<PluginId>` |
| `$PluginName$` | `<PluginDisplayName>` |
| `$PluginVersion$` | `<Version>` |
| `$EntryAssembly$` | `$(AssemblyName).dll` |
| `$EntryType$` | `<PluginEntryType>`（默认 `$(RootNamespace).AneiangYarpPluginEntry`） |

这样做的原因只有一个：`entryAssembly` 和 `entryType` 是最容易写错的字段，而它们的错误只有
在**别人装上去之后**才会暴露。改成从程序集属性推导，改名就不可能留下一个指向旧 dll 的清单。

想改作用域、能力、顺序、配置 schema，改 `plugin.manifest.json`；想改 id、显示名、版本，改 `.csproj`。

## 清单里三个容易填错的字段

- **`scopes`**：这个插件的配置能绑定到哪一层。`Route` 表示按路由绑（同一插件在不同路由上不同
  配置），`Cluster` 表示按集群绑。填错的表现是面板里选不到绑定目标。
- **`capabilities`**：宿主用它决定"这个插件需要什么"。`LoadBalancingPolicy` / `Transform` 这两个
  必须走 `IGatewayPluginHostServices.ConfigureHostServices`，写在 `ConfigureServices` 里会**静默不生效**
  —— 宿主的容器在插件启用前就已经冻结了。
- **`order`**：中间件顺序。内置插件占了 50–640（`service-discovery` 50、`waf` 100、`rate-limit` 200、
  `circuit-breaker` 300、`compression` 350、`request-retry` 400、`response-cache` 450、`proxy-log` 500、
  `traffic-metrics` 600、`cluster-metrics` 610、`fault-injection` 640）。模板选了 750，让新插件默认排在
  内置插件之后；要让你的插件**先于**某个内置插件执行，把 order 调到它前面。

## 加配置

模板插件不需要配置，所以 `schemas` 是空数组。要加配置：

1. 写一份 JSON Schema，放进 `plugin.manifest.json` 的 `schemas`：
   `{ "version": 1, "reference": "my-plugin/v1", "configJsonSchema": "{...}" }`
   —— `configJsonSchema` 是**字符串**，里面的引号要转义。
2. 面板的绑定编辑器会拿这份 schema 校验并渲染表单。
3. 需要改结构时加一个 **新的** `version`，同时实现 `IPluginConfigurationMigrator` 做旧配置迁移；
   不要原地改 v1 的 schema —— 已存的绑定不会重新校验，它们只会在下次保存时莫名其妙地失败。

可配置插件的参考实现见 `Aneiang.Yarp.Plugin.Compression`（小且完整）。

## 版本兼容

本模板引用 `Aneiang.Yarp.Plugin.Abstractions` **2.3.0.40**。契约程序集不随插件分发
（`ExcludeAssets="runtime"`）—— 宿主始终使用自己的那一份，所以插件与宿主的兼容性就是这条包引用的
版本约束。宿主版本与契约版本同号，兼容矩阵见仓库 `docs/plugins/plugin-compatibility.md`。
