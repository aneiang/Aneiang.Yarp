# Demo Playground

一个只为验证链路而存在的插件：**面板里存进去的每个字段，都会在响应上留下看得见的痕迹**。

所以它同时测三件事——插件市场能不能装、启用后能不能真的进管线、配置改完能不能立刻生效。
哪一环断了，看一眼响应就知道断在第几步（见下面的「对照表」）。

## 它做什么

按绑定配置对匹配到的路由做事，三种模式：

| `mode` | 行为 | 用来验证 |
| --- | --- | --- |
| `stamp` | 往响应上加 `X-Demo-*` 头，请求照常转发 | 中间件真的在管线里、配置真的到了插件手里 |
| `echo` | **不转发上游**，直接把插件读到的配置当 JSON 返回 | 配置的读取与反序列化，且不依赖上游是否活着 |
| `silent` | 什么都不做 | 对照组：绑定在、插件启用，但请求完全不受影响 |

可用字段（就是 `plugin.json` 里那份 schema，面板的绑定编辑器按它渲染）：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `enabled` | 开关 | 关掉后绑定保留，但不再产生效果 |
| `mode` | 下拉 | `stamp` / `echo` / `silent` |
| `badge` | 文本 | 写进 `X-Demo-Badge` 的标记，用来确认命中的是哪条绑定 |
| `samplePercent` | 数字 | 0 = 全量生效；1–100 = 只对这一比例的请求生效（取值并集：0 或 1–100） |
| `delayMs` | 数字 | 转发前人为延时，0–3000 |
| `skipPaths` | 逗号分隔 | 路径命中这些前缀时跳过 |
| `responseHeaders` | 多行对象 | 逐条附加的响应头 |
| `echo` | 嵌套对象 | echo 模式的 `statusCode` 与 `pretty` |

这一份字段是刻意挑的：布尔、枚举、整数、文本、字符串数组、对象数组、嵌套对象、取值并集
各来一个，正好把配置编辑器的每种控件都走一遍。

schema 的写法（支持哪些关键字、两种 `anyOf` 语义、版本怎么升）见
[插件配置 schema](https://yarp.aneiang.com/docs/#plugin-config-schema)。

## 目录

| 文件 | 作用 |
| --- | --- |
| `DemoPlayground.csproj` | 构建时把 `plugin.manifest.json` 渲染成 `plugin.json`（身份字段只写一次） |
| `plugin.manifest.json` | 插件清单 + 配置 schema。**schema 的唯一定义就在这里** |
| `DemoPlaygroundPlugin.cs` | `IGatewayPlugin` 入口：声明 manifest、把中间件挂进代理管线 |
| `DemoPlaygroundMiddleware.cs` | 干活的地方：从网关快照读本路由的绑定配置，按配置动手 |
| `DemoPlaygroundConfig.cs` | 配置的强类型投影，每个字段都有安全默认值 |

## 装上去

```bash
# 1. 发布成一个目录（目标框架必须和宿主一致，示例网关是 net8.0）
dotnet publish samples/DemoPlayground/DemoPlayground.csproj -c Release -o samples/DemoPlayground/publish
```

2. 面板 →「插件市场」→ 右上「安装外部插件」→ 目录填：
   `D:\Codes\Peson\aneiang-yarp\samples\DemoPlayground\publish`
3. 卡片上的开关打开（首次会加载程序集，健康状态变成 Healthy 才算进管线）
4. 「插件配置」→ 选一条路由 → 绑定 `demo-playground` → 按需填配置 → 保存

想立刻看到效果，绑到 `sample-local-service-path0`（`/api/local-service/{**catch-all}`），
前提是 `SampleLocalService` 在集群目标地址上跑着。

## 验证

```bash
# stamp：300ms 延时 + 两个自定义头
curl -i http://127.0.0.1:5200/api/local-service/ping
```

期望看到（顺序无关）：

```
X-Demo-Plugin: demo-playground
X-Demo-Route: sample-local-service-path0
X-Demo-Mode: stamp
X-Demo-Badge: hello-demo
X-Demo-Delay: 300
X-Demo-Extra: one
```

```bash
# echo：改成 mode=echo 再打一次，返回体是插件读到的配置
curl -i http://127.0.0.1:5200/api/local-service/ping
```

绑定的 `enabled` 关掉、或把 `mode` 改成 `silent`，上面这些头应当一个都不出现——
这两步是验证「停用真的停用」，比验证「生效」更容易被忽略。

## 对照表：现象 → 断在哪

| 现象 | 意思 |
| --- | --- |
| 市场里看不到这个插件 | 没装上，或 `plugin.json` 校验没过（卡片上会有原始错误） |
| 开关打不开 / 健康是 Unhealthy | 程序集没加载成功，看网关日志里的 `failed to load` |
| 启用正常但响应没有任何 `X-Demo-*` | 中间件没进管线：绑定没启用、绑到了别的路由，或 `mode` 是 `silent` |
| 有头但值是旧的 | 配置没发布到快照，刷新绑定再看 `ConfigVersion` |
| `echo` 返回的 `config` 缺字段 | 存的时候没提交这个字段，不是插件丢了 |

## 两个设计取舍

**schema 只写在 `plugin.json` 里。** 宿主的插件市场、绑定编辑器、保存时的校验，
读的都是 `plugin.json` 里的 `schemas[].configJsonSchema`；入口类里再声明一份，
迟早会有一份先改，然后面板显示的和后端校验的对不上。

**为了拿自己的配置，这个插件引用了 `Aneiang.Yarp`。** 外部插件目前没有契约层面的
「读我这条绑定配置」入口，只能像内置插件一样从网关快照（`IGatewaySnapshotPublisher`）
里按 `routeId` + `pluginId` 找。`csproj` 里两个 `ProjectReference` 都加了
`ExcludeAssets="runtime"`：这两个程序集必须在宿主的默认加载上下文里解析，
插件目录里放第二份 `Aneiang.Yarp.dll` 会得到两个不同的 `IGatewaySnapshotPublisher` 类型，
依赖注入永远查不到，表现是插件加载成功但一个头都不加。

## 已知限制

- **改完代码要重启网关再升级。** 停用插件后加载上下文进入 `UnloadPending`，
  程序集文件仍被占用，此时升级会失败在 `Access to the path 'DemoPlayground.dll' is denied`。
  等回收（或重启）之后再升级即可。
- 只支持 `net8.0`。宿主如果是 `net9.0`，把 `TargetFramework` 改掉并重新发布。
- 只声明了 `Route` 作用域。要绑到集群，`scopes` 里加 `Cluster`，并把中间件改成读
  `ClusterPlugins`（集群级没有单请求语义，通常用来做负载策略而不是改响应）。
