# AneiangYarpPlugin

An out-of-tree gateway plugin. It builds into a folder the host can pick up: your assembly, its
dependencies, and a generated `plugin.json`.

```bash
dotnet build -c Release
# → bin/Release/net8.0/plugin.json  (what the host reads)

dotnet publish -c Release -o out
# → out/plugin.json + everything it needs; install this folder, not the project directory
```

## 装进宿主

两条路，选一条：

**面板安装**（不用重启）：插件中心 → 安装 → 填**网关所在机器上的**发布目录路径（`dotnet publish`
出来的那个 `out/`）。对话框收的是服务器路径，不是上传文件。

**拷目录**：把 `out/` 整个目录放到 `<网关内容根>/plugins/<插件 id>/`（默认路径，可用
`Gateway:Dashboard:PluginDirectory` 改），然后重启宿主。

装完状态是 `Discovered`：**已安装、已启用，但还没加载**（这是刻意的 —— 一个有问题的程序集不该让
网关起不来）。两种方式让它开始处理请求：

- 重启宿主 —— 启动时会把所有已启用的外部插件加载进来，状态变 `Loaded`；
- 不想重启，就在插件中心把它**停用再启用**一次，同样会走到 `Loaded`。

装完仍然是 `Discovered` 时，面板会显示"已启用"，但那只是登记状态，请求还不经过它。

宿主会做这些检查，任何一条不满足都会报错 —— 而不是等你上线之后才发现：

| 检查 | 时机 | 不满足时的现象 |
|:--|:--|:--|
| `id` 必填，且匹配 `^[a-z0-9][a-z0-9._-]{0,63}$` | 发现 | `InvalidManifest`，错误信息写在面板上 |
| `id` 不与内置插件重名 | 安装 | 409，拒绝覆盖 |
| `name` 必填、`version` 是语义化版本 | 发现 | `InvalidManifest` |
| `scopes` 至少一个（`Route` 或 `Cluster`） | 发现 | `InvalidManifest` —— 没有绑定目标，装了也没用 |
| `entryAssembly` 是相对路径、文件就在 `plugin.json` 旁边、且能被读成 .NET 程序集 | 发现 | `InvalidManifest`，提示你发布而不是装项目目录 |
| `entryType` 必填 | 发现 | `InvalidManifest` |
| `schemas` 存在（可为空数组）；每个 `version` ≥ 1 且不重复；`configJsonSchema` 非空、可解析、根是 `"type": "object"` | 发现 | `InvalidManifest` —— 否则面板会渲染出一个没有字段的表单 |
| `entryType` 实现 `IGatewayPlugin`，且实例的 `PluginId` 与清单 `id` 一致 | 启用 | `LoadFailed`，错误信息写明两个 id |

`$ref` / `$defs` / `if` / `patternProperties` 这些写法不报错，只在安装时记一条 warning：它们不生效，
但你的清单可能是在给别的工具用。

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
入口类里**没有**第二份 `Manifest`：外部插件的元数据只从 `plugin.json` 读，写两份只会让其中一份
悄悄失效。

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

## 配置：schema 和代码是同一件事的两半

模板默认就带一份能用的配置，三个字段：

| 字段 | 类型 | 作用 |
|:--|:--|:--|
| `enabled` | boolean | 关掉这条绑定，不删配置 |
| `headerName` | string | 写哪个响应头 |
| `headerValue` | string | 写什么值 |

验证路径很短：启用插件 → 给某条路由加一条绑定 → 请求该路由 → 响应里出现你填的头。没绑定时插件
仍然在管线里，写的是默认值（`X-Plugin-Badge: hello from the plugin`），所以"插件到底跑没跑"第一时间
就能看出来。

改配置就是改 `plugin.manifest.json` 里的 `configJsonSchema`，然后：

1. `configJsonSchema` 是**字符串**，里面是一份 JSON Schema，引号要转义（`json.dumps(...)` 或让编辑器
   转义，别手打）。
2. 同名属性写进 `AneiangYarpPluginConfig`，面板存什么，`System.Text.Json` 就绑什么。两边名字对不上
   不会报错，只会永远读到默认值 —— 这是这类插件最常见的"配置不生效"。
3. 结构需要改的时候加一个**新的** `version`，同时实现 `IPluginConfigurationMigrator` 做旧配置迁移；
   不要原地改 v1 的 schema —— 已存的绑定不会重新校验，它们只会在下次保存时莫名其妙地失败。

配置在中间件里**按请求**从 `IGatewaySnapshotPublisher.Current` 读，不在构造函数里缓存：绑定是可以在
不重建管线的情况下修改的，缓存了就得重启。完整的关键字支持范围、字段怎么映射到控件、`anyOf` 的两种
语义，见官网文档 `plugin-config-schema`（仓库 `docs/pages/advanced/plugin-config-schema.md`）。

可运行的最小参考实现：`samples/DemoPlayground`（枚举、整数区间、字符串数组、对象数组、嵌套对象都覆盖了）。

## 版本兼容

本模板引用 `Aneiang.Yarp.Plugin.Abstractions` 和 `Aneiang.Yarp`，两者都是 **2.3.0.40-preview.1**，
并且都带 `ExcludeAssets="runtime"`。契约与核心程序集不随插件分发 —— 宿主始终使用自己的那一份，
所以插件与宿主的兼容性就是这条包引用的版本约束。宿主版本与契约版本同号，兼容矩阵见仓库
`docs/plugins/plugin-compatibility.md`。

第二个引用（`Aneiang.Yarp`）只是为了 `IGatewaySnapshotPublisher` 这一个类型。插件要读绑定配置就需要
它；只做转发改写的插件可以去掉，包体也会更小。
