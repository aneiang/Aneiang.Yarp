# 交接文档 · Dashboard 插件引导优化（2026-11-10）

> 用途：在 CodeBuddy 中打开本仓库后，先读这份文件即可接上上下文，无需翻聊天记录。
> 状态：代码已改完并通过编译，**浏览器实测未做**（用户自行验证）。

---

## 1. 项目背景

`D:\Codes\Peson\aneiang-yarp` — Aneiang.Yarp，基于 **YARP 2.3.0** 的反向代理网关，Dashboard 为 **Razor Class Library**（MVC Views，不是 Razor Pages）。

- 路由前缀：`/apigateway`（`DashboardRouteConvention`）
- 静态资源：`/_content/Aneiang.Yarp.Dashboard/...`
- i18n：`Infrastructure/I18n/{zh-CN,en-US}/*.json`，**中英必须同步**，缺键会显示原始 key
- 全局 `window.__()` 第二参数是**插值 params 对象**，不是 fallback
- Razor 中 CSS 的 `@media`/`@keyframes` 需转义成 `@@media`/`@@keyframes`

---

## 2. 本次会话解决了什么

### 起因
用户反馈：*"页面要开插件才有数据，我现在不知道"* → 进一步暴露插件页面的四个结构性问题。

### 调研结论（事实，别再重新查）

**10 个内置插件，按作用域分两组：**

| 作用域 | 插件 |
|---|---|
| Route（7） | rate-limit、waf、request-retry、response-cache、compression、proxy-log、traffic-metrics |
| Cluster（3） | service-discovery、cluster-metrics、circuit-breaker |

**改造前"两组 8 不是同一批"**（用户最大困惑点，现已消除）：
- 路由绑定弹窗 8 个 = 全部 Route（含 traffic-metrics，不含 service-discovery）
- 插件中心原来只有 8 个页签 = 有配置页的（含 service-discovery，不含 traffic-metrics）
- 真正"隐身"的是 **cluster-metrics** 和 **circuit-breaker**：既不在路由绑定列表，也没有配置页
- 改造后：插件中心 10 个页签全覆盖，无隐身插件

**10 个插件按用途是 6 + 1 + 3：**
- 6 个纯配置：rate-limit、waf、request-retry、response-cache、compression、service-discovery
- 1 个配置 + 监控：proxy-log → `/logs`
- 3 个纯观测：traffic-metrics → `/traffic`、cluster-metrics → `/cluster-metrics`、circuit-breaker → `/circuits`

**核心概念（页面上必须讲清的那句话）：**
> 启用插件 ≠ 插件生效。**绑定才是真正的开关。**
> ON + 已绑定 = 生效；ON + 无绑定 = 零生效零数据；OFF + 有绑定 = 绑定保留但失效。

绑定生效的三层过滤在 `src/Aneiang.Yarp.Core/.../GatewaySnapshotCompiler.cs:55-61`：绑定自身 enabled → 插件总开关 → 目标路由/集群存在。

---

## 3. 改动清单（按批次）

### 批次一 · 两个 Bug 修复

**1) 插件中心绑定路由支持批量绑定**
- `wwwroot/js/modules/plugin-binding-manager.js`：作用域 `<select>` 改 `multiple`；多选走 `DashboardApi.endpoints.batchCreateBindings`（后端 `/api/plugin-bindings/batch-create` 早就存在，**零后端改动**），单选仍走 `createBinding`；新增"覆盖已绑定"开关。

**2) 集群健康状态显示反了**
- 根因**不是**颜色映射，而是读了不存在的字段 `c.healthCheck.enabled`（`DashboardClusterResponse.cs:210-223` 的 `HealthCheckInfo` 只有 `active`/`passive`）→ 凡配置了健康检查的集群恒显示"已禁用"。
- `dashboard-cluster-metrics.js`：改为按 `unhealthyCount > 0 ? 异常 : healthyCount > 0 ? 健康 : 未知` 渲染三态。
- `dashboard-clusters.js`：两处整体健康判定改为 `unhealthyCount > 0` 优先（原 `healthyCount > 0` 会掩盖部分故障）。

### 批次二 · 数据来源五态诊断条

- **新增** `wwwroot/js/core/dashboard-datasource.js` — `DashboardDataSource.mount()`，五态推导：
  - `getPlugin` 抛错 → **未安装**
  - `enabled === false` → **未启用**（主按钮直连 `POST /api/plugins/{id}/toggle`）
  - `bindingCount === 0` → **未绑定**
  - 已绑定无数据 → **等待中**（`startWatch`/`stopWatch`，15s × 8 次自动观测）
  - 有数据 → **就绪**（收成单行摘要，渲染时不输出次要按钮）
  - 支持 `emptyContainerId`：未就绪时把"加载中"换成空状态
- `Views/Shared/_DashboardLayout.cshtml`：`.plugin-hint-banner` → `.ds-bar` / `.ds-empty`
- 4 个 partial 换成 `<div class="ds-host" data-plugin="..." data-scope="...">`
- 4 个页面 cshtml 接入 `mount()`，并修正 `hasData` 判据：
  - cluster-metrics 排除 `colspan` 占位行（否则空表被当成"运行中"）
  - traffic 用 `> -` 且非空
  - circuits 用 `circuit-refresh-time` 徽标
- `I18n/*/core.json`：新增 22 个 `datasource.*` 键

### 批次三 · 插件页面整改（按设计稿 07 板四条）

保留原「分组页签 + 面板」结构，**没有**改成 11 张卡片。四条改进点：

1. **作用域徽标常驻** — 页签按钮 + 面板标题都带 `● 路由 / ● 集群`（`.pc-scope--route/cluster`）
2. **一句话作用** — 新增 `pc.about.<pluginId>` 共 10 条，写"让什么发生变化"而非复述插件名；`dashboard-plugin-center.js` 的 `REGISTRY.about` 作中文兜底，`dashboard-plugins.js` 表格行内新增 `.plg-about`
3. **生效条件 + 当前绑定数** — `.pc-head` 头部块第二行常驻"生效条件：需启用 + 绑定到至少 1 个路由/集群"，右侧四态：未启用 / 未绑定任何目标 / 已绑定 N 个·全部已停用 / 已绑定 N 个·M 个生效中（复用 `onLoaded` 数据，不额外请求）
4. **配置与监控双入口** — 面板右端主操作（启用插件 / 添加绑定 / 检查绑定状态，按状态只出一个）+ 查看监控；未生效时监控渲染为 `.pc-head__locked` 虚线态，提示"插件生效后才会有数据"。`dashboard-plugins.js` 也加了 `MONITOR_PAGES` 映射（4 个页面）的监控图标入口。

**顺带修掉的：**
- `dashboard-plugin-center.js` 的 `REGISTRY` 补齐到 10 条（原 11 条，含已删除的分布式限流） → cluster-metrics / circuit-breaker 不再是黑盒，后端零改动（它们的 schema 早就在 `builtin://plugins/{...}/schemas/config-v1.json`）
- 新增 `Views/Dashboard/Partials/_PluginSubNav.cshtml`，插件管理 / 插件配置 / 插件资源三入口统一子导航
- 三个监控页（Traffic / ClusterMetrics / Circuits）清掉失效的 `pc-feature-config` 残块与多余的 `plugin-binding-manager.js` 引用
- 侧栏命名区分：插件管理（总览开关）/ 插件配置（绑定与参数）/ 插件资源（资源占用）

---

## 4. 文件索引

**新增**
```
src/Aneiang.Yarp.Dashboard/wwwroot/js/core/dashboard-datasource.js
src/Aneiang.Yarp.Dashboard/Views/Dashboard/Partials/_PluginSubNav.cshtml
```

**改动（核心）**
```
wwwroot/js/modules/dashboard-plugin-center.js      REGISTRY 11 条 + pc-head 头部块
wwwroot/js/modules/dashboard-plugins.js            .plg-about 列 + MONITOR_PAGES + 作用域列
wwwroot/js/modules/plugin-binding-manager.js       批量绑定 + onLoaded 回调 + 空态文案
wwwroot/js/modules/dashboard-cluster-metrics.js    健康三态
wwwroot/js/modules/dashboard-clusters.js           unhealthyCount 优先
Views/Dashboard/PluginCenter.cshtml                页签簇 + .pc-head 样式
Views/Dashboard/Plugins.cshtml                     统一表格样式
Views/Dashboard/PluginResources.cshtml             接入子导航
Views/Shared/_DashboardLayout.cshtml               ds-bar + 侧栏命名 + .plg-about
Infrastructure/I18n/{zh-CN,en-US}/{core,plugins}.json
```

---

## 5. 验证方式

```bash
# 语法
node --check src/Aneiang.Yarp.Dashboard/wwwroot/js/modules/dashboard-plugin-center.js
node --check src/Aneiang.Yarp.Dashboard/wwwroot/js/modules/dashboard-plugins.js

# 双语对齐（结果应为 diff: []）
cd src/Aneiang.Yarp.Dashboard/Infrastructure/I18n
node -e "const z=require('./zh-CN/plugins.json'),e=require('./en-US/plugins.json');
console.log('diff:',Object.keys(z).filter(k=>!(k in e)).concat(Object.keys(e).filter(k=>!(k in z))))"

# 编译
dotnet build src/Aneiang.Yarp.Dashboard/Aneiang.Yarp.Dashboard.csproj -v q --nologo
```

启动示例网关（端口以 `samples/SampleGateway` 实际输出为准，登录 `admin / demo123`）：
```bash
ASPNETCORE_ENVIRONMENT=Development dotnet run --project samples/SampleGateway
```

**当前状态：`dotnet build` 0 警告 0 错误；浏览器实测未做。**

---

## 6. 待办

1. **浏览器实测**（用户自行验证）重点看：
   - 插件配置页页签簇是否一行放得下（4 组共 11 个页签，窄屏会换行）
   - `pc-head` 右侧按钮在 1280 / 1440 下是否挤压标题
   - 深色模式下 `.pc-head__icon` 的 `#f1f5f9` 底色是否突兀
2. **设计稿里还有一块没落**：三入口"合并"目前只是加了统一子导航，尚未真正收敛成一个菜单项（用户之前认可先做低成本的，合并最后再说）
3. 若后续要做：插件绑定弹窗里按作用域过滤目标列表时，可复用 `PluginModule.MONITOR_PAGES` 的思路建一张 `SCOPE_TARGETS` 映射

---

## 7. 设计稿位置

Ardot 画布：`https://ardot.tencent.com/file/745879846128380`
- 01–05：数据来源四态诊断条、插件卡片用途、状态判定表、数据来源总览
- 06：插件全景图（11 个插件 / 两组 8 的区别 / 谁在隐身）
- 07：插件中心重构（上文批次三那四条）

---

## 8. 2026-11-10 追加：移除分布式限流（rate-limit-redis）

按用户要求整套删除 `Aneiang.Yarp.Plugin.RateLimit.Redis`，本地限流 `rate-limit` 保留。

删除内容：
- 项目 `src/Aneiang.Yarp.Plugin.RateLimit.Redis/`（插件 + 中间件 + Redis Lua 存储 + `IDistributedRateLimitStore`）
- `Aneiang.Yarp.sln` 项目条目与 12 行构建配置；`Aneiang.Yarp.Dashboard.csproj` 的 ProjectReference
- `DashboardServiceCollectionExtensions` 的两个 DI 注册与 using
- `GatewayPluginExecutionPlan.RedisRateLimitByRoute`（记录定义、Compile、Empty 参数）与 `RedisRateLimitExecutionConfig`
- 前端：插件中心 REGISTRY 条目、`pc-tab-panel`、`dashboard-capabilities.js` 映射
- i18n 6 个文件共 33 个键（zh/en 各半）；README 中英两侧的插件表格行与"11 个插件"计数改为 10
- CHANGELOG 未改：它记录历史发布，**不该被回写**

---

## 9. 2026-11-10 追加：移除全站「页面说明条」（page-help-bar）

用户要求去掉插件总览顶部那条蓝底说明（"本页看清每个插件是干什么的…"），并"类似这种提示"一并去掉。

移除清单（共 10 处页面头 + 2 处 CSS + i18n 键）：
- 视图：`Plugins`、`PluginCenter`、`PluginResources`、`Clusters`、`Routes`、`Settings`、`Notifications`、`Partials/_LogsPanel`、`Partials/_CircuitsPanel`、`Overview`（后者用 `.ov-help` 变体）
- CSS：`_DashboardLayout.cshtml` 的 `.page-help-bar / .help-close / .collapsed` 整块；`Overview.cshtml` 的 `.ov-help` 整块
- i18n：`core.json` 删 `page.help.clusters|routes|plugins|pluginResources|logs|circuits|settings|overview`，`plugins.json` 删 `pc.help`，`notif.json` 删 `notif.page.help`（zh/en 同步，键对齐已校验）
- **保留**：`config-alert-bar`（配置应用失败）、`ov-proxy-alert`（同上）、`ds-host` 数据来源四态诊断条 —— 这三类是有状态/带操作的反馈条，不是静态说明

校验：全部 i18n JSON 可解析、zh/en 键集合一致、`dotnet build Aneiang.Yarp.Dashboard` 0 警告 0 错误。

---

## 10. 2026-11-10 追加：产品规划（docs/product/yarp-product-roadmap.md）+ v2.4 全部落地

### 10.1 规划文档

`docs/product/yarp-product-roadmap.md`，10 章。结论一句话：**产品只覆盖了"装得上"，没覆盖"敢上线"**；最危险的不是缺功能，而是**"填了不报错也不生效"**。

关键判断都带代码证据，不是愿望清单：

| 结论 | 证据 |
|---|---|
| 不能做流量治理 | `PluginCapability` 只有 `RequestMiddleware / ProxyPipeline / Dashboard / BackgroundService`，没有 LB Policy / Transform 扩展点 |
| 一个测试都没有 | 全仓 0 个 xUnit 用例（`tests/Aneiang.Yarp.Regression` 是控制台程序，不是测试） |
| 指标出不去 | 无 `/metrics`；`LockFreeStatistics` 只在 metrics / proxy-log 插件绑定时才有写入 |
| 策略名是静默炸弹 | 四个 `*Policy` 字段是自由文本；全仓 grep `AddCors\|AddRateLimiter` 零命中 |
| 多实例无方案 | 无配置版本号、无 CAS、无 leader 选举 |

路线图分 v2.4（敢用）/ v2.5（协作）/ v2.6（生态），本章只落地 v2.4。

### 10.2 v2.4 六项（全部完成）

**① 策略名校验 + 9 个内置策略**
- 新增 `Infrastructure/Policies/GatewayPolicyCatalog.cs`：`IGatewayPolicyCatalog` / `GatewayPolicyCatalog` / `GatewayPolicyDefaults` / `AddGatewayPolicies()`
- `PolicyResolutionStatus` 有第三态 **`Unverifiable`** 而不是二元判断 —— `RateLimiterOptions.PolicyMap` 与 `OutputCacheOptions.Policies` 是 internal，没有公开枚举 API，谎报"Unknown"会误杀宿主自定义策略
- 授权走 `IAuthorizationPolicyProvider.GetPolicyAsync`（权威：null 即真未注册），CORS 走 `ICorsPolicyProvider`
- 内置：`gw-anonymous`、`gw-authenticated`、`gw-cors-open`、`gw-rl-100pm/1000pm/10ps`、`gw-cache-30s/5m/1h`
- **补 `app.UseRateLimiter()`**（`MapMetricsEndpoint` 那段之前就得调）—— 这是本轮最容易漏的一环：策略注册了但中间件没进管道，等于换个地方继续静默
- 前端：`dashboard-form-builder.js` 的 `POLICY_FIELD_KINDS` 把四个字段短路成下拉；未注册值原样保留并标注"（未注册，保存时会被拒绝）"；"自定义名称…"走 `window.prompt`
- `GET {prefix}/api/config/policies` 返回四类清单 + `meta.unverifiable = ["rateLimiter","outputCache"]`

**② 配置预检**
- 新增 `Modules/GatewayConfig/Services/ConfigurationValidator.cs`：`ValidationSeverity` / `ConfigValidationIssue(Severity, Field, Message, Hint)` / `ConfigValidationReport(Ok, Errors, Warnings)`；三方法 `ValidateRouteAsync / ValidateClusterAsync / ValidateAllAsync`
- `POST api/config/validate`（`JsonException` 也转成报告，`field` 取自 `ex.Path`）、`GET api/config/integrity`
- 前端 `dashboard-config-editor.js` 的 `validate()` 改 async：schema → 服务端预检 → `_renderValidationReport()` 渲染带 `field` chip 的卡片
- 陷阱：`RouteConfig.Timeout` 是 `TimeSpan?`、`ClusterConfig.HttpRequest.Version` 是 `Version`、`Metadata` 是 `IDictionary<string,string>` —— 三者都不是 string

**③ 变更差异预览**
- `_describeChanges()` 用人话列变化（`删除目标节点 d1：http://…` / `匹配路径：/a → /b` / `策略 RateLimiterPolicy：不启用 → gw-rl-100pm`）
- 含"删除"的项额外 `window.confirm`；差异同时写进配置历史 summary
- 理由：JSON 双列 diff 对运维不友好，"删了什么"才是他们要确认的东西

**④ 版本号 + 乐观并发**
- `GET api/config/state` → `{ version, lastModified, routes, clusters }`
- `ConfigControllerBase.TryRejectStaleWrite()`：读 `X-Config-Version` 头或 `expectedVersion` 查询参数，不一致返回 `409` + `currentVersion`
- `dashboard-api.js`：`isConfigMutation()` 判定后自动注入头，成功后 `setTimeout(loadConfigVersion, 0)` 回读，`init()` 里预热
- **取舍**：采样式（保存前一刻读版本）而非严格线性化 CAS —— 脚本 / gRPC 注册路径不能被打断

**⑤ Prometheus `/metrics`**
- `Infrastructure/Observability/`：`GatewayMetricsCollector`（无 DI、`ConcurrentDictionary` + `Interlocked`、`MaxTrackedSeries = 300` 上界，超出并入 `route="other"`）、`GatewayMetricsMiddleware`（从 `IReverseProxyFeature` 取 route/cluster）、`GatewayMetricsRenderer`（追加需要 DI 的 gauge）
- 指标：`gateway_requests_total{route,cluster}`、`gateway_request_duration_seconds`（13 桶 + `_sum` + `_count`）、`gateway_responses_total{class}`、`gateway_inflight_requests`、`gateway_uptime_seconds`、`gateway_metrics_dropped_series`、`gateway_build_info`（剥掉 `+<sha>` 后缀）、`gateway_routes/clusters_configured`、`gateway_config_version`、`gateway_circuits{state}`
- `DashboardUseOptions` 新增 `UseMetricsCollection` / `MapMetricsEndpoint` / `UseRateLimiterMiddleware`，默认全开
- Grafana 模板：`docs/observability/grafana-dashboard.json`（8 面板，`datasource` + `instance` 两个变量）

**⑥ 测试基线**
- 新增 `tests/Aneiang.Yarp.UnitTests`（xUnit，net9.0，已加入 `Aneiang.Yarp.sln`）
- **117 个用例全绿，`dotnet build Aneiang.Yarp.sln` 0 警告 0 错误**
  - `YarpJsonConfigTests`（18）：就覆盖用户之前遇到的 `Cannot convert string '' to boolean`
  - `ConfigurationValidatorTests`（60+ 断言点）：含"同一 payload 内新建集群不误报"、`Destinations.d1.Address` 级字段定位
  - `GatewayMetricsCollectorTests`：直方图累积性、`_sum` 单位、负耗时钳制、标签转义、基数上界
  - `GatewayPolicyCatalogTests`：`Unverifiable` 语义、大小写敏感、`AddGatewayPolicies` 与宿主 `AddCors` 共存
- 踩坑记录：YARP 的配置类型是 **init-only record**，测试里不能 `route.Timeout = ...`；`AuthorizationPolicy` 不接受空 requirement 列表（`must have at least one requirement`）；`CorsPolicyProvider` 是 internal，测试改用自定义 `ICorsPolicyProvider` 桩

### 10.3 版本与文档

- `src/Directory.Build.props`：`2.3.0.30` → **`2.4.0`**
- `CHANGELOG.md`：新增 `[2.4.0]` 完整条目
- `README.md` / `README.zh-CN.md`：新增「Observability & Production Readiness (2.4.0)」/「可观测性与生产化（2.4.0）」章节

### 10.4 下一步（v2.5 依赖本轮成果）

v2.5 的「配置灰度与回滚」「多实例控制面」直接依赖本轮的版本号 + 预检能力，所以必须先有 v2.4。开工前需要拍板的仍是路线图 §10 那四个问题（多实例锁定"单控制面"、v2.4 不加新插件等）。

**当前状态：`dotnet build Aneiang.Yarp.sln` 0 警告 0 错误；`dotnet test` 117/117 通过；浏览器端（策略下拉、预检面板、确认弹窗、`/metrics` 抓取）未实测 —— 按用户要求由用户自行验证。**

---

## 11. 2026-11-11 追加：2.4.1 —— 把 v2.4 的欠账补上

### 11.1 起因

用户让我对照规划自查"还有哪些没实现"。结果是 v2.4 的六项**主体都在，但 4 项没达到自己写的验收标准**，另有 3 个规划里明确要求的文档/度量动作完全没做：

| 欠账 | 证据 |
|---|---|
| 预检与版本号只覆盖 2 个入口 | `IConfigurationValidator` 只注入 `ClusterConfigController` / `RouteConfigController`；自注册、导入、回滚、AI 助手 4 条写路径全部绕过 |
| CI 不存在 | 全仓无 `.github/`、无 `gitlab-ci.yml` |
| 连接池指标零代码 | `grep connection_pool` 在 Observability 目录无命中 |
| Grafana 没进 nupkg | 只在 `docs/` 下，`PackagePath` 未配置 |
| README 无"不做"清单 | §7 要求"写进 README" |
| ADR 只有表格 | 无 `docs/adr/` |
| TTFR 零埋点 | §8 原话"现在连 TTFR 都测不出来" |

### 11.2 核心改动：护栏下沉到核心库

问题本质不是"少写了一个校验调用"，而是**校验放在了错误的层**。四个写路径不可能各写一份校验，那是复制 bug。

方案：核心库声明契约，Dashboard 提供实现。

```
Aneiang.Yarp/Validation/ConfigurationGuard.cs
  IConfigurationGuard            ← 核心库声明
  ConfigurationGuardResult/Issue ← 与 Dashboard 的 ConfigValidationReport 同构
  PermissiveConfigurationGuard   ← 默认实现：全部放行
  ConfigConcurrency              ← 版本头解析 + IsStale + 409 载荷，两个控制器共用

Dashboard: ConfigurationValidatorGuard : IConfigurationGuard  ← 适配 IConfigurationValidator
           services.Replace(ServiceDescriptor.Singleton<IConfigurationGuard, ConfigurationValidatorGuard>())
```

**为什么默认实现是"放行"而不是"拒绝"**：只用核心库、不装 Dashboard 的部署，行为必须一行不变。装 Dashboard 之后才接管校验 —— 这样守卫是"能力"而不是"破坏性变更"。

**四条写路径的接入点**：

| 路径 | 接入方式 |
|---|---|
| `POST/PUT api/gateway/*` | 控制器注入 `IConfigurationGuard`，写前 `CheckRouteAsync` / `CheckClusterAsync`；`PUT clusters/{id}` 校验的是**合并后**的集群而非补丁 |
| 批量注册 `batch/register` | 逐条校验（不是全批拒绝）—— 响应本来就是逐条报成功，一条坏的不该丢掉另外 49 条 |
| 导入 `ImportFullConfigAsync` | `ValidateBatchAsync(全部路由, 全部集群)`，不通过则整批拒绝 |
| 回滚 `RollbackAsync` | 同上。**快照可能是几个月前的**，指向已改名集群或已删策略，"恢复配置"把故障放大是最坏情况 |
| AI `create_route` | `ExecuteWriteToolAsync` 解析 `IConfigurationGuard` 后传入；模型输出是不可信输入 |

### 11.3 新增 `ValidateBatchAsync`（批量语义）

单条校验对导入/回滚是错的：`ValidateRouteAsync` 拿 `_config.GetCluster(id)` 判断集群存在，而快照里的集群**还没落库**，于是每一条路由都会被判"集群不存在"。

批量版把"本批次要创建的集群"算作已存在：

```csharp
var clusterWillExist = proposedClusterIds.Contains(route.ClusterId!) || _config.GetCluster(route.ClusterId!) != null;
```

并且给字段加归属前缀：`Clusters.c1.Destinations.d1.Address`。30 个集群同时出错时，没有前缀就是 30 条一模一样的 `Destinations.d1.Address`。

### 11.4 连接池指标：`MeterListener` + `System.Net.Http`

2.4.0 我判断"YARP 不暴露连接池所以做不到"—— 这个判断错了。.NET 8+ 的 `System.Net.Http` 有内置 Meter，`http.client.open_connections` 带 `http.connection.state`(active/idle) 与 `server.address`。

`UpstreamConnectionMonitor` 的要点：

- **两种语义都要处理**：.NET 8 的 `open_connections` 是同步 UpDownCounter（测量值是**增量**），更新版本是 observable gauge（**绝对值**）。用 `instrument.IsObservable` 区分 —— 否则在 .NET 8 上会得到一个负数或恒为最后一次增量的假值。
- **`gateway_upstream_metrics_available`**：明确区分"池是空的"与"这个运行时根本不发这类指标"。少了这一个 gauge，`0` 会被误读。
- 主机数上限 100，超出并入 `host="other"`；observable instrument 要显式 `RecordObservableInstruments()` 才会触发，放在渲染路径里。

### 11.5 TTFR 打点

`gateway_serving`（0/1）+ `gateway_first_success_seconds`。口径：起点 = 收集器构造（≈ 进程启动），终点 = 第一个 **2xx/3xx** 代理请求完成。4xx 不算 —— "起来了但一条都没转发对"正是要抓的状态。

实现细节：用 `Interlocked.CompareExchange(ref _firstSuccessSeconds, -2, -1)` 做一次性占位，避免两个并发请求都写入。

### 11.6 CI

`.github/workflows/ci.yml`：

- **单作业、不做 SDK 矩阵**。库多目标 `net8.0;net9.0`，而 .NET 9 SDK 能同时编译两者；8.0.x SDK 根本编不了 net9.0 目标 —— 矩阵会直接红。
- **"用例数 < 100 直接 fail"**：防止过滤条件写错导致 0 个用例也通过。
- **打包后断言 Grafana 模板在包里**：`unzip -l | grep observability/grafana-dashboard.json`。这类"仓库里有、包里没有"的 bug 只有装包才发现。

已本地验证：`dotnet pack` 后包内同时存在 `content/observability/` 与 `contentFiles/any/any/observability/` 两份。

### 11.7 文档

- 中英 README 各加「我们明确不做的事」/「What We Deliberately Do Not Build」，6 条 + 理由 + 替代方案。
- `docs/adr/`：ADR-001 单控制面、ADR-002 开放 LB/Transform、ADR-003 限流只留扩展位，外加 `README.md` 索引与"什么该写 ADR"。
- `docs/product/yarp-product-roadmap.md`：新增 §0.1 交付状态总览（22 行逐项状态），§3 矩阵按实际翻转覆盖度，§4 五个痛点各加状态块，§6 补"复盘"段，§7/§8/§9 标注落地位置，§10 记录四个问题的执行结果，新增 §11 变更记录。

### 11.8 状态

- `dotnet build Aneiang.Yarp.sln` → 0 警告 0 错误
- `dotnet test` → **158/158 通过**（117 → 158，新增：批量校验 7、并发契约 15、守卫适配器 4、TTFR 8、连接池 7）
- 版本 `2.4.0` → `2.4.1`，CHANGELOG 增 `[2.4.1]`
- 浏览器端（策略下拉、预检面板、409、`/metrics` 里的新指标）**仍未实测**，按用户要求由用户自行验证

---

## 12. 2026-11-11 追加：2.5.0 —— 插件扩展点 + 权重灰度

### 12.1 先修掉一个会毁掉灰度的隐患

`DashboardClusterMapper` 在集群没有显式负载策略时返回 `LoadBalancingPolicy = "Default"`。这个值被 `buildYarpCluster` 原样写进 JSON 编辑器，用户只要"改一个权重再保存"，集群里就多出 `LoadBalancingPolicy: "Default"` —— **YARP 没有这个策略名**，集群随即解析失败。

改成返回空串，编辑器不再写回该字段。这个 bug 与 2.4.0 打的"策略名静默炸弹"是同一类，只是换到了 LB 字段上。

### 12.2 #1 插件框架扩展：决策稿的假设被推翻

ADR-2 原来写"给插件加一个容器构建期入口"，默认以为复用 `IGatewayPlugin.ConfigureServices` 就行。核实代码后否定：

```
PluginRuntimeDomain.PrepareAsync()
  ├─ var services = new ServiceCollection();   ← 新建，不是宿主那个
  ├─ plugin.ConfigureServices(services);
  └─ services.BuildServiceProvider()           ← 插件自己的容器
```

宿主容器在 `AddAneiangYarp*` 返回时已定型，YARP 的 `LoadBalancingPolicy` 单例在首次解析时把 `IEnumerable<ILoadBalancingPolicy>` 拍成字典。**在 `ConfigureServices` 里注册 LB 策略是确定不生效，不是"可能不生效"。**

实际做法：独立接口，不动既有签名（10 个内置插件一行没改）。

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

`AddAneiangYarpDashboard` 最后一步扫描 `IGatewayPlugin` 描述符，对实现了接口的类型实例化一次并调用。三条硬约束：

1. **必须有无参构造函数** —— 此时没有 provider，构造注入不可用；缺失则启动即抛，不静默跳过
2. **只能注册，不能替换** —— API 只给 `IServiceCollection`，没有移除入口，ADR-2 第 3 条天然成立
3. **时机固定在容器构建前** —— 决策稿担心的"插件启动顺序"因此消失

`PluginCapability` 增加 `LoadBalancingPolicy` / `Transform` 两个值，但**声明不等于生效**：没真正注册就是一个解析不到的策略名，所以校验仍然以"策略名是否存在"为唯一判据。

### 12.3 #2 权重灰度：为什么不用 PowerOfTwoChoices

YARP 自带的 `PowerOfTwoChoices` 也读 `Weight`，但只把它当**两个随机节点之间的平局裁决**，实际比例会漂移。灰度决策不能建立在"大概"上，所以实现了平滑加权轮询（nginx 式）：

```
每轮：current[i] += weight[i]；取 current 最大的；current[picked] -= total
```

- 一个完整周期内每个节点被选中次数**精确等于** `weight / total`
- 交错分布，不会先连发 90 次再给 1 次
- `sum(current)` 恒为 0，单值不超过 `total`，不会溢出

关键实现细节：

- **状态按集群隔离 + 加锁**，但数组是预分配复用的，热路径零分配（`int.TryParse` 不分配）
- **目标集合变化时自动重置**。用 `ReferenceEquals` 逐项比较，不用拼字符串签名 —— YARP 配置重载会换掉整个 `DestinationState` 图，健康翻转会换掉子集，两种情况都能命中
- **全部权重为 0 回退到第一个可用节点**。权重表坏了不该变成 503 风暴
- **钉住优先于权重**：`X-Gateway-Canary: v2`（Tag）或 `: d2`（节点名），先按 id 再按 Tag 匹配，未命中就忽略（陈旧书签不该 503）

### 12.4 观测：按目标分组

灰度期间真正要回答的问题只有一个 —— "新节点拿到的份额对不对，错误率比老节点高多少"。路由级计数器答不了，所以加了第二维度：

- `gateway_destination_requests_total{cluster,destination}`
- `gateway_destination_failures_total{cluster,destination}`（只认 5xx）
- `gateway_metrics_dropped_destination_series`

数据来源是 `IReverseProxyFeature.ProxiedDestination`，读的是 `await _next(context)` **之后**的值 —— 之前它还没被负载均衡器填上。没选到节点（无健康目标、早期转发失败）不记账，避免造出幽灵序列。

Grafana 加两块面板 + 一个 `cluster` 变量，总数 13。

### 12.5 校验

`ConfigurationValidator.CheckWeights()`，四种失败模式：

| 情况 | 级别 | 为什么必须拦 |
|---|---|---|
| 权重不是非负整数 | Error（`Destinations.x.Metadata.Weight`） | 静默回退默认值，用户以为设了 |
| 全部权重为 0 | Error（`Destinations`） | 网关照常服务，全部流量压到第一个节点，只有翻访问日志才发现 |
| 写了 `Tag` 但策略不是 `Weighted` | Warning | 灰度标记完全无效 |
| 权重表配在非 `Weighted` 策略上 | 不报 | `PowerOfTwoChoices` 也读 `Weight`，这是合法配置 |

### 12.6 状态

- `dotnet build Aneiang.Yarp.sln` → 0 警告 0 错误
- `dotnet test` → **207/207 通过**（158 → 207）
  - `WeightedLoadBalancingPolicyTests`（23）：90/10 精确、99/1 精确、并发 400 次仍精确、canary 不拿第一个请求、权重 0 永不被选、全零回退、非法/负权重、自定义键与头名、表变更重置
  - `PluginHostServiceTests`（12）：贡献注册、非贡献忽略、抽象类型忽略、缺无参构造抛错、重复类型只实例化一次、能力分类
  - `ConfigurationValidatorTests`（+8）：权重表四种失败模式
- 版本 `2.4.1` → `2.5.0`；CHANGELOG 增 `[2.5.0]`；中英 README 各加「灰度发布」；ADR-002 补「实施记录」
- 浏览器端（权重列、灰度比例、钉住头、两块新面板）**未实测**，按用户要求由用户自行验证

### 12.7 v2.5 剩余

| # | 项 | 依赖 |
|:--:|:--|:--|
| 3 | 灰度应用 + 自动回滚 | 本轮的 `gateway_destination_failures_total` 已经够做阈值判断 |
| 4 | 指标告警阈值 | 同上 |
| 5 | OpenTelemetry 追踪 | 独立 |
| 6 | 故障注入插件 | 独立，可复用插件扩展点 |
