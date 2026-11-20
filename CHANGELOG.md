# 更新日志

## [2.3.0.40] - 2026-11-20

> **正式版**：`2.3.0.40` 已发布，API 与插件契约按本版固定，`dotnet` 会默认选中。
>
> - 模板 `dotnet new install Aneiang.Yarp.Templates`
> - 包 `<PackageReference Include="Aneiang.Yarp" Version="2.3.0.40" />`
> - Helm `version: 2.3.0-40` / `appVersion: "2.3.0.40"`
>
> **版本口径**：`2.3.0` 是 YARP 版本，`40` 是本项目版本。2.3.0.30 之后的所有改动（原计划的 2.4.0 / 2.4.1 / 2.5.0 / 2.6.0 四批）统一归入本版，不再分批发布；下文出现的 2.4.x / 2.5.x / 2.6.x 只是开发批次名。

### 本版一览

| 领域 | 交付内容 |
|:--|:--|
| 安全 | 19 项整改全落地：SQLCipher 真加密、会话改 cookie、CSP/HSTS、2FA 防重放、审计、登录限流 |
| 可观测 | Prometheus `/metrics`、OTLP 追踪（含导出连通性自检）、指标告警阈值、13 个 Grafana 面板 |
| 灰度 | `Weighted` 精确分流 + `X-Gateway-Canary` 钉住 + 自动放量与自动回滚 |
| 稳定性 | 故障注入插件；重试 / 熔断 / 压缩 / 缓存的中间件真正接入管线 |
| 平台 | RBAC 三角色、多环境隔离与两步提升、插件市场、OpenAPI 文档、Docker / Helm / `dotnet new` |
| 防火墙 | WAF 重写为声明式规则引擎（10 来源 × 8 操作符 × 5 动作）+ 卡片式编辑器 |
| 框架 | `net8.0;net9.0;net10.0` 三目标，插件契约保留 `net8.0` 下限 |

### 🛡 安全（19 项）

- **依赖漏洞**：改用 `Microsoft.Data.Sqlite.Core` + `bundle_e_sqlcipher` 2.1.11，把 `bundle_e_sqlite3` 从依赖图移除 —— 显式 pin 传递版本压不住 CVE，只能让它消失
- **数据库加密**：实测生效（`PRAGMA cipher_version` 有值、密文扫描搜不到哨兵串、错密钥报 `SQLITE_NOTADB`），并加启动自检：连接串带 `Password=` 而 provider 不能加密时，构造即抛
  - ⚠️ **破坏性**：旧版本写出的明文库升级后带 `Password=` 会打不开（26）。处置：去掉 `Password=`，或用 `sqlcipher_export()` 迁移
- **转发头**：由 `IStartupFilter` 注册并清空框架默认信任的 loopback；可信代理支持 CIDR，解析不了的条目单独告警而不是整份配置作废
- **凭据与会话**：密码改 PBKDF2-SHA256 210k 自描述格式；会话改 `HttpOnly` cookie 并**移除**响应体与 localStorage 里的 token（两者并存会让前者失效）；登录加双键滑动窗口限流；比较统一 `FixedTimeEquals`
- **浏览器侧**：补 CSP / XFO / XCTO / Referrer-Policy / HSTS
- **2FA**：`2fa/*` 收紧为 admin-only 且 `setup` 由 GET 改 POST；TOTP 加 `lastAcceptedCounter` 高水位防重放；逐账号 `TwoFactorSecret`
- **出站与审计**：Webhook 分三段校验（字面量、逐次尝试、关闭自动重定向）；日志与响应里的 URL 凭据脱敏；认证 / 授权 / 插件 / 2FA 全部写审计
- **JWT**：校验 `alg` 头并加 `jti` + 吊销表，logout 真正生效；密钥文件在 Unix 上 `0600`，读取时也收紧一次
- **插件边界**：.NET 没有可靠的进程内沙箱（`CollectibleLoadContext` 只解决卸载），因此改为契约化 —— 安装对话框显式警告，并把"插件 = full trust"写进文档；插件 id 白名单 + 目录边界按尾随分隔符比对（`/plugins-evil` 不再算 `/plugins` 的子目录）
- **分析器基线**：`CA5359`（客户端 gRPC 无条件跳过证书校验）改为默认校验，`AllowUntrustedGrpcCertificate=true` 才跳过；`CA5350` 是 RFC 6238 规定的 HMAC-SHA1，用带理由的 suppress 保留。基线归零后 Security 分类提升为 `error`

### 📡 可观测

- **Prometheus `/metrics`**（文本格式 0.0.4）：`gateway_requests_total`、`gateway_request_duration_seconds`（13 桶）、`gateway_responses_total`、`gateway_inflight_requests`、`gateway_uptime_seconds`，外加 `gateway_build_info`、`gateway_routes_configured`、`gateway_clusters_configured`、`gateway_config_version`、`gateway_circuits` 等需要 DI 的 gauge。采集点在代理管线内，**不依赖插件是否绑定**；基数上界 300，超出并入 `route="other"` 并计入 `gateway_metrics_dropped_series`
- **OTLP 追踪**：埋点在核心库（只 `new ActivitySource("Aneiang.Yarp")`，零新依赖），SDK 与 exporter 全落在 Dashboard —— 不开追踪的网关不背 exporter 依赖图
  - 分层 span：`GET → gateway.forward → gateway.plugin ×N → proxy.forwarder`；`TracedProxyPipelineBuilder` 让第三方插件零改动即被追踪
  - 采样按 traceId 哈希（同一链路结论一致，不会记了父丢子），入站 `traceparent` 的 sampled 标记优先；采样率可在面板运行期调整
  - 控制面请求不进追踪（面板、`_content`、`/metrics`、健康检查）；`TraceRingBuffer` 三重上限且**不持有 `Activity`**；不配 endpoint 也能看瀑布图；endpoint 不合法只记 `StartError`，网关照常转发
  - `/tracing` 页：状态带分三列（在跑吗 / 数据去哪 / 采样多少）、两行式链路列表带耗时条、瀑布图带时间轴与图例
- **导出鉴权与连通性自检**：三个来源按 header 名逐项合并（`Gateway:Dashboard:Tracing:Headers` > `Data/tracing-auth.json` > `OTEL_EXPORTER_OTLP_HEADERS`），接口只回脱敏值；`http/protobuf` 的 `/v1/traces` 由网关补齐（导出器不会自动追加）。追踪页「测试」按钮真实发一个空批次：401 与 404 分型（换凭据 vs 补路径）、gRPC 只看 `grpc-status` trailer 并先做 TCP 预检 —— 过去这些失败都表现为「连不上」
- **指标告警**：`ErrorRate` / `LatencyP99` / `CircuitOpen` 三类规则，范围可选 Global / Cluster / Route。判定顺序即产品语义：窗口填满 → 样本量够 → 越线 → 持续够久；`MinSamples` 以下不判断（一个失败请求就是 100%），无流量时不回判「已恢复」；通知走现有 7 家渠道，`Firing → Resolved` 也发一条，`RenotifyMinutes` 支持重提醒；规则与历史落盘 `metric-alerts.json`
- **新增指标**：`gateway_destination_requests_total` / `..._failures_total`（按目标）、`gateway_route_failures_total`（按路由 5xx）、`gateway_first_success_seconds` + `gateway_serving`（TTFR —— 「装好了但一条都没转发」以前在所有指标上都看不见）、上游连接池 `gateway_upstream_connections`。Grafana 模板随包提供，面板 11 → 13

### 🚦 灰度与稳定性

- **`Weighted` 策略**：nginx 式平滑加权轮询，权重取目标节点 `Metadata.Weight`（`0` = 已注册不接流量）；全部为 0 时回退第一个可用节点；目标集合变化时重置累加器；`X-Gateway-Canary` 可把某个真实用户先钉到新版本
- **自动放量与回滚**：状态机 `Observing → Paused / Completed / RolledBack / Aborted`，阶梯默认 10 / 30 / 50 / 100，窗口 120s、阈值 20%、最小样本 50。样本不足只 Hold 并延长窗口；基线自己也在挂时 Pause 而不是 Rollback（回滚既救不了火，还会销毁现场）；回滚精确还原权重键 —— 原本没有 `Weight` 的节点删除该键，而不是写死 1
- **故障注入**：延迟 / 错误码 / 连接中断三种注入，支持 `requiredHeader` 门与 `probability`。位置刻意在最内侧紧挨 forwarder，这样重试会重试它、熔断会记它、指标会记 5xx、告警会响、灰度会因此回滚；`X-Fault-Injected` 防止演练被误认成真实故障
- **写路径护栏下沉**：`IConfigurationGuard` 覆盖 Dashboard CRUD、客户端自注册、批量导入、历史回滚、AI 助手五条写路径；核心库默认 `PermissiveConfigurationGuard` 全放行，只引核心库的部署行为不变。回滚前先预检，避免"恢复配置"把故障放大
- **配置预检与体检**：`POST /api/config/validate` 每条问题带字段路径与 hint；`GET /api/config/integrity` 交叉检查孤儿集群、死路由、重复 `Match.Path`；另有变更差异预览（保存前用自然语言列出变化，含删除的二次确认）与 `X-Config-Version` 乐观并发（不一致 409）
- **策略预设**：授权 / CORS / 限流 / 输出缓存改为下拉 + 9 个内置策略；授权走 `IAuthorizationPolicyProvider`、CORS 走 `ICorsPolicyProvider`，其余因 `PolicyMap` 是 internal 返回 `Unverifiable` 而不是谎报未知；补上缺失的 `app.UseRateLimiter()`

### 🏗 平台

- **多环境隔离**：每环境一个 SQLite 文件（`prod` 用原连接串，其余派生后缀），存量部署零迁移、dev 库损坏不影响 prod，且不动任何表结构；`RuntimeEnvironment`（决定转发，启动即定死）与 `CurrentEnvironment`（决定面板读写）显式拆开 —— 切环境只切"你在编辑哪个"，否则"打开 staging 看一眼"就等于把生产流量切走
  - 发布守卫只放在 `DynamicConfigPublisher.Publish` 一个收口点（写入路径有五条，漏一条就是一次事故）；切换时重载工作集，否则会把上一个环境的配置写进新环境；查询缓存按环境分键
  - 顶部切换器：编辑运行环境时灰色 + `LIVE`，编辑其它环境时琥珀色并写明「仍在转发 prod」；单环境部署时整个控件不显示
- **环境差异与两步提升**：`GET /api/environments/diff` 按字段渲染成 `Destinations: 2 → 5`，只比会改变流量行为的字段；`promote/prepare` 返回差异 + 问题清单 + 5 分钟单次令牌，`promote/apply` 必须带令牌且用过即废。指纹哈希的是**两侧配置本身**而不是渲染后的差异 —— 用差异做指纹时，批准后偷改内容差异完全不变，审的和应用的不是同一份东西
- **RBAC 三角色**：`viewer` / `operator` / `admin`，切分依据是爆炸半径而非资历。viewer 按 HTTP 方法拒绝（路径白名单会随新页面失效），operator 按路径拒绝且默认允许（新增运维端点自动可用）；角色走 JWT `role` claim（降权需重新登录，正是收紧时想要的语义）；`Users` 为空时退回单账号行为，无法归属的身份按隐式管理员处理；403 带 `read-only` / `admin-required` 两个 reason 码；面板用 `data-requires-write` / `data-requires-admin` 隐藏写操作（CSS 规则，后渲染的工具栏同样生效）
- **部署三件套**：`dotnet new aneiang-yarp`、`deploy/docker/Dockerfile`（非 root、`HEALTHCHECK`、数据卷指向 `/app/data`）、`deploy/helm/aneiang-yarp`（chart 拒绝渲染 `replicaCount > 1`、`Recreate` 而非 RollingUpdate、gRPC 独立 Service、`checksum/config` 注解、随机凭据 + `keep`）。CI 新增 `deployables` 作业：模板真编译、chart 真渲染并断言守卫、镜像真构建
- **管理 API 文档**：从 `EndpointDataSource` 生成 OpenAPI 3.1（95 路径 / 110 操作），`/api-docs` 是零依赖独立页（不引 CDN、不引 Swagger UI）。`[FromBody]` 曾因命名空间判定被当成服务丢掉，`requestBody` 从 8 个恢复到 37 个

### 🧩 插件体系

- **插件模板**：`dotnet new aneiang-yarp-plugin` 生成可直接拷进 `plugins/` 的外部插件；`plugin.json` 由 `plugin.manifest.json` + csproj 属性在构建时渲染（id / entryType / version 填错只有别人装上去才暴露）；契约程序集用 `ExcludeAssets="runtime"`，否则会出现两个互不认识的 `IGatewayPlugin`
- **模板开箱可用**：插件模板补上 `Aneiang.Yarp` 引用、默认 schema 与中间件示例，按请求从 `IGatewaySnapshotPublisher.Current` 读绑定配置 —— 此前生成的插件能开表单，但填了不生效
- **管线顺序改读 manifest `order`**：原来宿主写死 8 个内置 id 决定谁能参与，不在列表里的插件配好、绑好、面板亮着，中间件却永远不执行（`compression` 正好在这个洞里，外部插件更是全都进不去）
- **配置页支持外部插件**：`REGISTRY` 降级为内置元数据，`/api/plugins` 里未登记的插件合并进「扩展」组，市场装好的插件终于能在面板上配置
- **Tab 栏不再随插件数增长**：长度由「在用的插件」决定 —— 有绑定的上浮到首屏，其余收进「更多扩展 N」选择器，会话内可固定。30 个插件时不再出现 275px 的标签墙
- **安装校验加强**：scopes 非空、`entryAssembly` 存在且可读、`schemas` 必填且版本唯一、`configJsonSchema` 可解析且根为 `type: object`、id 不得与内置冲突。语义性问题（`$ref` / `if`）只警告不拒装 —— 那是渲染器的能力边界，不是清单写错了
- **`configJsonSchema` 有文档了**：代码里补全 XML 注释，官网新增《插件配置 schema》一页，写明可用子集、`anyOf` 的双语义，以及 `$ref` / `if` / `patternProperties` 会被静默忽略
- `<prefix>/plugin-market` 页：内置与外部同屏、来源徽标区分、按本机目录安装/升级/卸载、三种空状态各给一条下一步、安装失败不关对话框
- 示例插件 `samples/DemoPlayground`：`stamp` 加响应头证明在管线里、`echo` 直接回配置 JSON 证明反序列化、`silent` 作对照

### 🎨 界面

- **插件配置页的面板改为由注册表生成**：视图里手写 10 个面板而注册表有 11 个插件，漏掉的正好是 `fault-injection`，`renderConfigPanel` 找不到宿主就静默返回 —— 点开一片空白，控制台也什么都没说
- **一批「后端有接口、面板没入口」的功能补了前端**：全量体检、`?focus=<id>` 深链定位、熔断阈值定义表、预设的删除与应用、批量绑定启停与删除、配置快照队列指标
- **链路追踪页重构**：状态/去向/采样拆成三列并补上「等重启生效」；链路列表改两行式（请求 + `路由 → 集群 → 目标` 跳链），耗时右对齐并配长度条；瀑布图加时间轴
- **WAF 换成规则卡片式编辑器**：一条规则一张卡片、标题读成一句话；首屏只留「检测什么 / 在哪里比 / 比什么 / 怎么处理」，操作符按来源过滤；新增六个模板、上下移与复制、五色左边线
- 通知管理页新增「指标告警」页签；移除全站 10 处静态说明条（保留带状态与操作的提示条）

### 🔧 架构

- 新增 `ConfigurationValidator`（写路径统一"校验通过才落盘"）、`GatewayPolicyCatalog`（`AddGatewayPolicies` 追加式注册，与宿主已有 `AddAuthorization` / `AddCors` 共存）、`Infrastructure/Observability/`（采集器 + 中间件 + 渲染器）、`ConfigConcurrency`（版本头解析与 409 载荷共用一份）
- 新增插件配置编辑器扩展点 `PluginConfigEditors`：通用 schema 渲染器遇到嵌套「对象数组」只能输出 `Item 1`，WAF 的 `rules[] → conditions[]` 是两层；插件可自带编辑器接管整个表单，两个调用方不改一行就切过去
- 新增 `PluginCapability.LoadBalancingPolicy` / `Transform` 与 `IGatewayPluginHostServices`：`ConfigureServices` 跑在插件自己的容器里，那里注册的 LB 策略 YARP 永远看不见
- i18n 46 个重复键按命名空间归属合并，且**保留屏幕上实际生效的那份值** —— 顺手"取更通顺的那份"等于在没人知道的情况下改了界面文案

### 🐛 修复

- **文件上传被 WAF 100% 拦成 403**（Gitee #IKW95C）：检测正则里的 `\B--` 命中 multipart 的 `------WebKitFormBoundary…`；中间件读 body 不看 `Content-Type`，PDF 与图片都被当文本扫；`EnableBuffering()` 不带上限。三处一并解决
- **WAF 首个请求偶发 403**：`RegexOptions.Compiled` 的首次编译耗时计入 50ms 匹配预算，超时按命中处理。改为类型初始化时预热，把一次性成本从"误拦一个请求"变成"启动慢一次"
- **重试耗尽后真实 5xx 被吞**：`X-Retry-Count` 写在响应体拷贝之后，此时响应已启动 → 抛 `InvalidOperationException`，客户端拿到 500，真正的 502/503 丢失
- **权重热更新不生效**：按 `DestinationState` 引用判断"目标是否变了"，而 YARP 重载保留实例只替换 `Model`，于是权重表在第一次请求后被永久冻结，没有任何日志。灰度每一步改的都是权重，这个 bug 会让整个灰度"看起来在跑、实际一步没动"
- **集群接口返回不存在的 `"Default"` 策略名**，JSON 编辑器原样写回后集群就解析不了
- **其他**：空连接串时各环境共用同一库文件；面板路由/集群列表跨环境串味；插件在 `ConfigureServices` 注册扩展点被静默丢弃；「备份数据库」在中文界面显示英文（i18n 键从未添加）；停用再启用后插件状态恒为 `UnloadPending`

### 📝 文档

- 新增 `deploy/README.md`（三种部署方式、共用约束、部署后验证清单）、`docs/adr/ADR-001 ~ ADR-004`、`docs/plugins/plugin-compatibility.md`（11 个插件的矩阵 + 校验表 + 能做/不能做）、`docs/observability/tracing.md`（优先级、路径补齐规则、各后端填法）
- 官网新增《插件配置 schema》规范页；修正插件开发文档中与代码不符的字段名（`Id` / `Scope` → `PluginId` / `Manifest` / `scopes`）
- 两个 README 新增部署、插件开发、管理 API、WAF、多环境、告警、追踪、灰度、故障注入共九节，并补上「我们明确不做的事」6 条
- 路线图按实际状态对齐（v2.5 6/6、v2.6 6/6），清理文档里三个互相矛盾的测试数

### ✅ 测试

- 用例 **765** 个，net9.0 与 net10.0 两个运行时各跑一遍；`dotnet build` 0 警告 0 错误
- 主要新增：多环境 28、RBAC 54、追踪 53、告警 47、WAF 86、灰度 30、故障注入 31、权重 25、OpenAPI 18、插件契约 12、插件宿主 12、i18n 6、端点覆盖 2、重试 2
- 这些用例钉的多是「坏了也不报错」的不变量：`Weighted` 在同一 `DestinationState` 上被就地替换后仍生效、`X-Retry-Count` 必须在响应启动前写入、面板保存出来的 config 不丢字段且 `monitor` 不会被归一化改回 `enforce`、端点覆盖测试既要求每个 `/api/` 端点有调用点也要求白名单不腐烂、i18n 每个键只能定义在一个域文件里
- 真实网关复核（非单测）：WAF 19 项断言（含 multipart 字节数一致）、故障注入 11 项、多环境 11 项、追踪（假 OTLP 接收端抓到完整 span 树）、插件模板安装 → 启用 → 绑定 → 响应头变化、OpenAPI 95 路径

### 🏗 构建与 CI

- **`net10.0` 三目标**：`src/` 下 17 个工程改为 `net8.0;net9.0;net10.0`，每个包产出三份 `lib/` 资产。加目标不是破坏性变更，删才是 —— `Plugin.Abstractions` 的 `net8.0` 是插件生态下限，删掉会让所有外部插件工程的下一次 `restore` 直接 `NU1202`
- **net10 的 API 迁移**：`ForwardedHeadersOptions.KnownNetworks` 与 `HttpOverrides.IPNetwork` 在 net10 废弃（`ASPDEPR005`），替代品 `KnownIPNetworks` + `System.Net.IPNetwork` 在 net8/9 不存在，用三处 `#if NET10_0_OR_GREATER` 的小 helper 分开实现，而不是给整个工程加 `NoWarn`
- **SDK 与 CI**：新增 `global.json` 锁 `10.0.100`（`rollForward: latestFeature`）。原来没有 pin 时 MSBuild 取机器上最高的 SDK，CI 因此跑到 11.100.0 并在 `pack` 阶段报 `NU5050`（`logo.png` 重复落位）。三个 job 各加一步校验 SDK 与 `global.json` 一致，让漂移立刻失败而不是变成一条看不懂的打包错误
- 打包路径统一为正斜杠（Linux 上 `..\logo.png` 只是一个文件名）；修复测试工程调用旧 `TryInstall` 签名的编译错误，并为"id 撞内置插件"补了用例
- **模板**：网关模板删掉全仓零引用的 `Deployment:HotReload` 配置，README 更正密钥持久化与注册接口鉴权的表述，`Dockerfile` 改 `ARG DOTNET_VERSION`（`--Framework net9.0` 时原镜像写死 8.0 会构建失败）；`--Framework` 增加 `net10.0`

### 📊 交付进度

| 批次 | 项 | 状态 |
|:--|:--|:--|
| v2.5 #1 | 插件扩展点（LB 策略 / Transform） | ✅ `IGatewayPluginHostServices` |
| v2.5 #2 | 权重灰度 | ✅ 精确分流 + 热更新 + 按人钉住 |
| v2.5 #3 | 灰度应用 + 自动回滚 | ✅ 状态机 + 归因判定 + 精确回滚 + 面板 |
| v2.5 #4 | 指标告警阈值 | ✅ 三类规则 + 滑动窗口 + 无数据保护 |
| v2.5 #5 | OpenTelemetry 追踪 | ✅ 分层 span + 确定性采样 + 导出自检 |
| v2.5 #6 | 故障注入 | ✅ 延迟 / 错误码 / 连接中断 |
| v2.6 #1 | 多环境隔离与一键提升 | ✅ 隔离切换 + diff + 两步提升 |
| v2.6 #2 | RBAC | ✅ 三角色 + 逐账号 2FA + 403 reason |
| v2.6 #3 | Docker / Helm / `dotnet new` | ✅ 三件套 + CI `deployables` |
| v2.6 #4 | 插件市场 | ✅ 模板 + 兼容矩阵 + 契约测试 + 市场页 |
| v2.6 #5 | 管理 API OpenAPI | ✅ 95 路径 / 110 操作 + 零依赖渲染页 |
| v2.6 #6 | 自定义 WAF 规则 | ✅ 10 来源 × 8 操作符 × 5 动作 + 卡片编辑器 |

---

## [2.3.0.30] - 2026-09-20

> 正式版发布：2.3.0.30-preview.1 全部特性转正（插件化架构完全落地、响应压缩插件、Redis 分布式限流、原生 YARP 配置直编、配置应用失败感知），并完成品牌与打包升级。功能变更明细见下方 [2.3.0.30-preview.1] 条目。

- 修复 `2.3.0.30-preview.1` 版本中已知BUG


## [2.3.0.30-preview.1] - 2026-08-28

> ⚠️ **预览版本** - 此版本为预发布版本,用于收集反馈和验证稳定性。生产环境请使用正式版 2.3.0.25。

> 插件化架构方案完全落地：插件驱动导航 + Compression 插件 + Redis 分布式限流 + 原生 YARP 配置直编（移除 Native Adapter）

### 📋 预览版说明

本预览版包含自 2.3.0.25 以来的全部变更,主要新功能:
- 插件化架构完全落地(11个第一方插件)
- 新增响应压缩插件
- 新增 Redis 分布式限流插件
- 移除 Native Adapter,集群/路由直接编辑原生 YARP JSON 配置(表单/JSON 双模编辑器)
- 配置应用失败感知与可视化告警

**反馈渠道**:
- GitHub Issues: https://github.com/aneiang/Aneiang.Yarp/issues
- 反馈截止: 2026-09-04 (7天后发布正式版)

### 🚀 新功能

- **插件驱动导航**：所有第一方插件（11 个）实现 `ConfigureDashboard()` 贡献导航项；新增 `PluginDashboardInitializer` 在启动时收集插件贡献的菜单和小组件；`_DashboardLayout.cshtml` 在"插件中心"组动态渲染插件导航（含 `data-plugin-requires` 显隐控制、i18n、服务端 fallback 标题），移除全部硬编码插件菜单项
- **响应压缩插件**（`Aneiang.Yarp.Plugin.Compression`）：Route 作用域代理管道插件，按 MIME 白名单 + 最小响应尺寸自动启用 Gzip/Brotli 压缩；可配置压缩级别（Optimal/Fastest/NoCompression）；正确设置 Content-Encoding/Vary 头并移除 Content-Length；配套管理页面（`/compression`）与绑定管理
- **分布式限流插件**（`Aneiang.Yarp.Plugin.RateLimit.Redis`）：基于 Redis Lua 原子脚本实现 FixedWindow/SlidingWindow/TokenBucket 三种算法；多实例网关全局精确限流；429 响应携带 Retry-After 和 X-RateLimit-* 头；通过反射可选加载 StackExchange.Redis（宿主未引用 Redis 时自动降级直通）；配套管理页面（`/rate-limit-redis`）与绑定管理
- **多 Provider 服务发现确认**：`ServiceDiscoveryRefreshService` 已完整支持 Consul（健康实例过滤）、Nacos（healthyOnly + hosts 解析）、Eureka（JSON + port.$ 格式 + UP 状态过滤）、Kubernetes（ServiceAccount Bearer token + endpoints 解析）、HttpJson、Static 六种模式
- **表单/JSON 双模配置编辑器**：集群/路由的新增与编辑统一为同一双模弹窗；表单模式由 JSON Schema 驱动，卡片分组渲染（基本信息 + 对象/数组独立卡片），支持 `patternProperties` 动态键对象（如 Destinations）与数组项编辑；JSON 模式带格式化/压缩/校验
- **配置应用失败感知**：实现 YARP `IConfigChangeListener` 捕获配置重载失败 + 发布前 `IConfigValidator` 预校验，错误经 `IProxyConfigErrorStore` 存储（reload/prevalidate 来源去重，reload 优先）；概览页告警条 + 集群/路由列表页提示条与行内错误标记，可通过 `GET /api/config/apply-errors` 查询

### 🔧 架构改进

- **移除 Native Adapter（Plan A 落地）**：删除全部原生插件适配器，集群/路由配置回归 YARP 原生 JSON Schema（`ClusterSchema.json`/`RouteSchema.json`/`ConfigurationSchema.json`）；用户通过双模编辑器直接编辑原生配置，消除适配层的字段覆盖与行为不一致（此前 `allclusterprops` 测试集群暴露的无效 HealthCheck Policy/Affinity 配置即由此显现）
- **表单模式国际化**：表单/JSON 模式切换、校验面板、配置对比弹窗共 19 个 i18n key（zh-CN + en-US）补齐；修复 `__('key') || 'fallback'` 在键缺失时 fallback 永不生效的问题（`__` 对缺失键返回键名本身）
- **方案文档执行状态标注**：`docs/plans/route-cluster-plugin-architecture.md` 头部添加全部里程碑执行状态表

### 🐛 缺陷修复

- **通配符主机绑定解析**：`launchSettings.json` 的 `applicationUrl` 支持 `http://*:port` / `http://+:port`（Kestrel 双栈通配符绑定）；此前 `new Uri()` 直接抛 `UriFormatException` 导致未解析地址原样注册到网关；通配符现与 `0.0.0.0`/`::1` 同等参与"监听所有网卡"判定与 LAN IP 解析
- **列表详情行宽度**：集群/路由列表展开详情行与空状态行 `colspan` 与表头列数对齐，修复展开区域宽度不铺满

### 📡 gRPC 注册通道完善（Aneiang.Yarp.Grpc / Aneiang.Yarp.Client）

- **客户端补齐 Phase 2 注册能力**：`GatewayRegistrationOptions` 新增 `MatchPaths`（多路径 → 多路由共享集群）、`LoadBalancingPolicy`（PowerOfTwoChoices/Random/RoundRobin/LeastRequests）、`ExtraDestinationAddresses`（额外目标全部加入集群负载均衡）、`Metadata`（自定义元数据）；gRPC 注册请求完整传递上述字段（此前仅发送单路径/单目标，LB 策略与 metadata 被丢弃）
- **客户端 SDK 公开查询/更新 API**：新增 `GetServicesAsync`（查询网关已注册服务）与 `UpdateDestinationsAsync`（整体替换集群目标集）公开方法（gRPC 模式专用）
- **心跳失败自动恢复**：客户端心跳连续失败 3 次后自动触发重新注册（覆盖网关重启丢失动态注册状态场景），成功后心跳继续；gRPC 心跳异常补齐日志
- **gRPC 注销多路径残留修复**：多路径注册生成的 `{serviceId}-path{N}` 路由此前注销时全部残留；现按服务 ID 精确匹配（含 path 后缀校验）逐一移除
- **gRPC 心跳兼容多路径**：心跳按 `{serviceId}` 解析不到路由时回退解析 `{serviceId}-path{N}`，多路径注册的心跳不再失效
- **UpdateDestinations 保留 LB 策略**：gRPC 更新目标集时保留集群已有 LoadBalancingPolicy（此前 `TryAddCluster` 整体覆盖会静默重置为 null）


### 📝 补充说明

- **平滑重载设计决策**：进程级蓝绿切换（新进程健康检查后切流量）不适用于库形态（NuGet 嵌入宿主进程），采用进程内热重载（`PluginRuntimeDomainManager` DI 重注册 + 中间件原子重建）；零停机由宿主部署策略（K8s 滚动更新等）承担
- 新增 i18n key：compression/rateLimitRedis 相关（zh-CN + en-US，core.json + plugins.json）
- Release 全量编译实测 0 警告 0 错误（此前记录的 8 个警告已消除）；i18n 11 个文件 2334 个键 zh-CN/en-US 完全对齐


## [2.3.0.25] - 2026-07-14

> AI 智能运维 + WAF 防火墙重构 + 日志系统重构 + 核心架构拆解 + SignalR 实时推送 + 国际化重构

### 🚀 新功能

- **AI 智能运维模块**：内置 ChatBot，支持自然语言交互管理网关（创建路由、查询集群、配置策略等），基于 OpenAI 兼容 API，支持 SSE 流式响应和 Function Calling 工具链
- **AI 设置页面**：可配置 Provider、BaseUrl、API Key、模型名称、Temperature 等参数，支持千问/DeepSeek 等多种模型
- **AI 后台分析**：后台服务自动分析日志和配置，主动发现异常
- **AI 通知增强**：AI 自动为通知事件生成智能分析摘要
- **SignalR 实时推送**：Overview 页面通过 SignalR 实时广播流量和状态数据，替代 HTTP 轮询
- **WAF 规则引擎重构**：拆分为独立规则检查器（IP 访问控制、SQL 注入、XSS、路径遍历、请求大小限制、安全头检查），支持链式执行
- **日志系统重构**：请求/响应日志分离采集，支持日志采样、过滤、动态设置、SQLite 批量写入与聚合查询
- **日志设置服务**：运行时可动态调整日志级别、采样率、保留天数等参数
- **SQLite 迁移系统**：引入 `ISchemaMigration` 接口和 9 个迁移脚本，支持数据库结构平滑升级

### 🎨 改进

- **历史日志合并展示**：同一 traceId 的 ProxyRequest 和 ProxyResponse 合并为一行，展开后完整展示上游请求 → 下游请求 → 响应的数据流
- **历史日志条数调整**：合并后分页显示的总条数按配对比例估算，反映实际合并后的行数
- **Overview 页面重构**：全新 UI 布局，接入 SignalR 实时数据
- **通知系统增强**：新增冷却管理、预热服务、渠道/规则管理
- **路由列表集群过滤**：支持按集群 ID 筛选路由，包含“(none)”选项筛选未绑定集群的路由
- **QPS 图表自动刷新**：Overview 页 QPS 图表每 10 秒自动刷新
- **国际化重构**：i18n 资源从内联代码迁移到 JSON 文件，zh-CN 和 en-US 各 10 个语言包，便于维护和扩展
- **时间处理统一**：全项目统一使用 `DateTime.Now`，SQLite 查询添加 `localtime` 修饰符，修复时间偏移问题

### 🔧 架构

- **DynamicYarpConfigService 拆解**：将 2000+ 行的单体服务拆分为 `ClusterConfigManager`、`RouteConfigManager`、`DynamicConfigPublisher`、`DynamicConfigPersister`、`YarpConfigParser` 等独立组件
- **存储层改进**：新增 `ProxyLogMetaEntity`、`ProxyLogBodyEntity` 等实体，引入 `SqliteProxyLogAggregator`、`SqliteProxyLogBatchWriter` 等专用仓储
- **Dashboard 视图扩展**：新增 `DashboardViewLocationExpander` 支持模块化视图定位

### 🗑️ 移除

- **WAF 实时安全事件**：移除事件存储、持久化服务、AI 工具、独立页面，WAF 拦截仍保留日志和通知
- **Benchmarks 项目**：移除基准测试项目
- **部署配置**：移除 docker-compose、systemd、Windows 安装脚本（移至独立仓库）
- **CI 工作流**：移除 GitHub Actions 配置
- **GatewayTracing**：移除网关追踪选项和服务扩展

### 🐛 修复

- 历史日志页首次切换到“历史日志”标签时不自动加载数据（异步竞态条件）
- SQLite `strftime` 时间格式化偏移 8 小时（添加 `localtime` 修饰符）
- 全项目 `DateTime.UtcNow` 替换为 `DateTime.Now`，统一本地时间处理


## [2.3.0.24] - 2026-06-28

> Dashboard 功能完善 + 全站国际化适配 + 代码精简

### 🚀 新功能

- **Dashboard 支持完整 YARP 配置**：路由的 Host/Header/Query/CORS/Auth 匹配、集群的会话保持/健康检查/HTTP 配置等高级属性全部可用
- **请求先行日志**：请求一进来就显示在日志页，不用等响应返回
- **自动 CORS 和授权**：无需手动配置中间件，开箱即用

### 🐛 修复

- 配置修改后刷新页面不再丢失高级属性
- 差异对比面板不再显示 null，增删条目显示实际内容
- 导入配置后自动生成导入后快照
- 统计/历史/通知/部署等页面切换英文时不再出现中文

### 🗑️ 清理

- 移除热重载模块（`ConfigurationFileWatcher`）
- 精简日志系统，只保留请求/响应日志
- 移除运行模式页面的手动操作按钮

### 🎨 改进

- 16 个页面统一初始化逻辑，减少重复代码
- 日志页样式优化，自适应屏幕高度


## [2.3.0.22] - 2026-06-26

> 原生 YARP 配置完整保留 + 端口重复绑定修复 + 全局加载指示器 + JSON 宽松解析

### 🚀 新增

- **原生 YARP 配置完整保留**：通过 Dashboard 编辑/保存路由和集群时，所有高级属性（SessionAffinity、HttpClient、HttpRequest、完整 Match 条件、Auth/Cors/Timeout 策略等）完整保留，不再丢失
- **全局加载指示器**：API 请求时显示顶部进度条，加载失败提供重试按钮，离线状态自动检测
- **JSON 宽松解析**：配置编辑器和导入功能支持 JSON 注释（`//`、`/* */`）和尾逗号，兼容标准 YARP 配置文件风格
- **路由匹配大小写敏感**：Header / QueryParameter 匹配支持 `IsCaseSensitive` 选项

### 🐛 修复

- **端口重复绑定**：配置 `Kestrel:Endpoints` 时不再报 "address already in use" 错误
- **集群 HTTP Request 配置丢失**：集群详情中 HttpRequest 配置（ActivityTimeout、Version 等）现在正确显示
- **配置导入大小写不兼容**：导入配置时 `Routes`/`routes`、`Clusters`/`clusters` 等键名均可识别

### 🎨 优化

- 审计日志页添加加载动画
- 集群加载失败显示可重试错误提示

### 📝 文档

- README 添加公众号二维码
- 新增完整 YARP 配置参考示例 `docs/yarp_all.json`


## [2.3.0.21] - 2026-06-24

> 灵活端口与启动模式 + 健康检查 + 配置热更新 + 2FA 两步验证 + 企业化 UI 重构 + 前端性能优化 + 代码清理

### 🚀 新增功能

#### 灵活端口与启动模式
- **多端口监听**：通过 `Kestrel:Endpoints` 配置多个端点，每个端点绑定不同端口和 IP
- **5 种启动模式**：`Auto` / `AllInOne` / `Split` / `ProxyOnly` / `DashboardOnly`
- **端点角色路由**：`EndpointRouterMiddleware` 根据 `LocalPort` + 角色决定请求归属
- **角色枚举**：`Proxy` / `Dashboard` / `Admin` / `Health` / `All`
- **安全防护**：`RequireLoopbackForDashboard` 默认 true，公网绑定 Dashboard 启动失败
- **命令行快捷参数**：`--deployment split --proxy-url ... --dashboard-url ...`
- **完全向后兼容**：现有 `Urls` 配置自动生效，行为不变

#### 健康检查
- **3 个端点**：`/health`（综合）、`/ready`（就绪）、`/live`（存活）
- **依赖检查**：可配置是否检查数据库连接、YARP 配置加载
- **鉴权选项**：可选 IP 白名单、可选 Token 鉴权（`X-Health-Token` 头或 `?token=` 查询）
- **K8s 友好**：返回 JSON 包含 `status` / `checks` / `uptime` / `version` / `endpoints`

#### 配置热更新
- **FileSystemWatcher 主监听**：监听 `appsettings.json` 和 `appsettings.{Env}.json`
- **30s 兜底轮询**：防止编辑器 temp+rename 模式下事件丢失
- **500ms 防抖**：避免编辑器多次保存触发连续重载
- **失败自动回滚**：重载异常时恢复上次快照
- **配置快照**：保留最近 5 个版本，支持手动回滚（`.config-snapshots/` 目录）
- **端点变更检测**：Kestrel:Endpoints 变更时发出告警（不自动重启）

#### 2FA 两步验证（TOTP）
- **TOTP 验证器**：`TotpHelper` 生成 Base32 密钥 + `otpauth://` URI，支持 Google Authenticator 等验证器应用
- **登录页 2FA 适配**：登录时检测 2FA 启用状态，返回 202 时展示验证码输入框，按钮文字切换"登录"→"验证"
- **设置页 2FA 管理**：配置 2FA（生成密钥+二维码）、验证绑定、关闭 2FA、实时状态刷新
- **运行时状态持久化**：2FA 启用状态保存到 `twofactor-state.json`，重启不丢失
- **i18n 支持**：新增 `login.verify`、`login.verifying`、`config.twofactor*` 等中英文键

#### 系统健康监控
- **DashboardInfoResponse 扩展**：新增 CPU 使用率、内存工作集、总内存、GC 次数、线程数字段
- **DashboardInfoQueryService 增强**：实时计算 CPU%（处理器时间/运行时间）、GC 收集次数、线程数

### 🎨 UI/UX 企业化重构

#### 登录页重新设计
- **毛玻璃卡片**：`backdrop-filter: blur(20px)` + 半透明白色背景
- **品牌色渐变面板**：靛蓝→紫→蓝渐变，玻璃态图标，装饰圆圈
- **气泡动画背景**：24 个小气泡从底部上浮，6 色随机，左右摇摆+缩放
- **渐变登录按钮**：阴影 + hover 上浮效果
- **底部版本信息**：`v2.3.0 · MIT License · © 2026 Aneiang.Yarp`

#### 侧边栏菜单重构
- **分组折叠**：5 个分组（仪表盘/网关管理/监控运维/安全策略/系统管理），点击标题折叠/展开
- **当前页自动展开**：进入页面时自动展开所在分组，其余折叠
- **品牌区**：三色渐变图标 + 光晕 + 副标题
- **用户卡片**：渐变头像 + 右下角脉冲在线指示
- **底部操作**：图标按钮组（语言切换、退出登录）
- **字体调大**：body 14→15px，菜单项 13→14px，分组标题 10.5→13px

### ⚡ 性能优化

#### 前端资源优化
- **字体 TTF→WOFF2**：5 个 Inter 字体 1,590KB → 553KB（节省 65%）
- **缓存启用**：移除 `Cache-Control: no-store`，已有 `?v=版本号` 机制可安全缓存
- **压缩优化**：Brotli/Gzip 压缩级别 Fastest→Optimal，新增字体 MIME 类型
- **Monaco 精简**：删除 8 个不使用的 NLS 语言包（-1,747KB）+ 74 个 basic-languages 目录（-450KB）
- **按页面拆分模块**：17 个页面模块从 Layout 移至各页面 `@section Scripts`，每页减少 ~500-700KB
- **编辑器脚本按需加载**：8 个编辑器脚本从 Layout 移除，仅 Settings/Clusters/Routes 页加载
- **13 个页面清理重复核心脚本**：删除 ~97 行重复 `<script src>` 引用

### 🐛 修复

- **2FA 验证码错误返回码**：401→400，避免前端 API 客户端将其当作认证失效跳转登录页
- **2FA API 响应解包**：`Settings.cshtml` 中 `resp.data.secret` → `resp.secret`（DashboardApi 已自动解包）
- **dashboard-plugins.js 语法错误**：删除 `resetAll` 函数后的孤立重复代码块
- **DashboardApp 未定义**：在 `dashboard-core.js` 中定义 `DashboardApp` 对象（modules 注册表 + registerModule + navigateTo）
- **首页数据绑定**：`Overview.cshtml` 添加 `dashboard-home.js` + 直接 API 兜底渲染
- **loadSystemHealth API 调用**：`DashboardApi.getInfo()`（不存在）→ `DashboardApi.endpoints.getInfo()`
- **ServiceTabs 初始化时序**：DOMContentLoaded 已触发时立即执行而非注册监听

### 🧹 代码清理

- **前端 JS**：删除 48 条 `console.log`、277 条分隔线注释、59 条冗余注释（共 384 条）
- **后端 C#**：删除 42 条分隔线注释（`// ───` / `// ====`），保留 XML 文档注释
- **push-nuget.ps1 更新**：新增 2 个 Storage 项目、依赖顺序打包、`-SkipRestore` 参数

### 📁 新增文件

- `Infrastructure/Deployment/`：`DeploymentOptions`（5 种模式枚举）、`EndpointRoleResolver`、`DeploymentConfigValidator`、`ConfigSnapshot`、`DeploymentCli`
- `Infrastructure/Middleware/`：`EndpointRouterMiddleware`、`HealthCheckMiddleware`；`Infrastructure/HostedServices/`：`ConfigurationFileWatcher`、`KestrelEndpointChangeDetector`
- `Infrastructure/Auth/TotpHelper.cs`、`Modules/Dashboard/Controllers/DeploymentInfoController.cs`、`Views/Dashboard/Deployment.cshtml`、`wwwroot/js/modules/dashboard-deployment.js`
- `samples/SampleGateway/appsettings.examples/`：`AllInOne` / `Split` / `SplitWithHealth` / `ProxyOnly` 四份示例

### 📝 使用示例

```csharp
builder.Services.AddAneiangYarpDeployment(builder.Configuration);
app.UseMiddleware<EndpointRouterMiddleware>();   // 需在 UseAneiangYarpDashboard 之前
app.UseMiddleware<HealthCheckMiddleware>();
app.UseAneiangYarpDashboard();
```

双端口配置见 `samples/SampleGateway/appsettings.examples/appsettings.Split.json`（`Kestrel:Endpoints` + `Gateway:Deployment:EndpointRoles`）。

### 🔗 相关文档
- 设计文档：`docs/灵活端口与启动方案设计.md`

#### Dashboard "运行模式" 页面
- 新增菜单项：**系统管理 → 运行模式**（路径：`/{prefix}/deployment`）
- 展示内容：
  - **当前模式卡片**：Auto / AllInOne / Split / ProxyOnly / DashboardOnly 五种模式高亮
  - **运行摘要**：进程启动时间、运行时长（实时更新）、程序版本、环境
  - **监听端点表格**：名称/地址/端口/角色/是否公网/状态
  - **配置热更新状态**：启用状态、监听文件、防抖时间、兜底轮询间隔、失败回滚
  - **健康检查状态**：启用状态、端点路径（`/health` `/ready` `/live`）、鉴权方式、检查项
  - **安全告警卡片**：Dashboard/Admin/Health 端口暴露公网时红色高亮
  - **配置快照表格**：时间/触发/文件/查看详情
  - **手动操作区**：重新加载配置、创建快照、健康检查
- 后端 API：`GET /api/deployment/summary` 聚合所有运行时信息
- 数据来源：`DeploymentOptions` + `EndpointRoleResolver` + `IConfigSnapshotStore`


## [2.3.0.20] - 2026-06-13

> 里程碑版本：完整 WAF 防火墙、通知告警系统、健康检查面板、熔断/限流/重试 Dashboard、Storage 模块架构重构（上帝接口消除）、SQLCipher 数据库加密、数据库下载。README 中英文版大幅扩容，新增技术栈总览、中间件管道、插件系统、策略引擎、性能优化等章节。

---

### 🛡️ 新增功能

#### WAF 防火墙（全新模块）
- **WAF 中间件引擎**（`WafMiddleware`，424 行）：在 YARP 代理管道前拦截恶意请求
  - **IP 黑白名单**：支持精确 IP、CIDR 网段、通配符，白名单优先策略，内置正则缓存优化
  - **SQL 注入检测**：2 组预编译正则（SQL 关键字 + 注入模式），带 5ms ReDoS 防护超时
  - **XSS 跨站脚本检测**：检测 `<script>` 标签、`javascript:` 伪协议、事件处理器注入
  - **路径遍历检测**：识别 `../` 及 URL 编码变体、双重编码攻击
  - **请求限制**：可配置请求体大小上限（默认 10MB）、请求头数量上限、单头大小上限、URI 长度上限（4096）
  - **安全响应头注入**：自动添加 `X-Content-Type-Options`、`X-Frame-Options`、`X-XSS-Protection`、`Referrer-Policy`、`Content-Security-Policy`
  - **按路由级精细控制**：通过 YARP 路由元数据 `Waf:Enabled` 可单独为每个路由启用/禁用 WAF
  - 拦截时返回 403 JSON（含 `waf:true` 标记便于前端识别）
- **WAF 设置管理页面**（`Views/Dashboard/Waf.cshtml`）：全局开关、检测规则开关、IP 黑白名单 textarea、请求限制参数配置
- **WAF 设置持久化**（`WafSettingsPersistenceService`）：带内存缓存的读写分离，`SemaphoreSlim` 线程安全，`appsettings.json` 兜底
- **安全事件查看器**（`Views/Dashboard/Security.cshtml` + `dashboard-security.js`）：15 秒自动刷新，按事件类型过滤，攻击类型统计条形图，Top 攻击 IP 排行
- **安全事件环形缓冲区**（`WafEventStore`，`ConcurrentQueue`）：内存保留最近 1000 条，支持获取和清空
- **SecurityEventsController API**：`/api/security-events` — 最近事件查询、清空、测试事件触发、批量测试、统计摘要

#### 通知告警系统（全新模块）
- **多渠道通知**：支持配置多个 Webhook 端点，独立超时、重试次数设置
- **告警规则引擎**：按事件类型独立开关（熔断器打开、重试耗尽、WAF 拦截、代理错误、限流触发）
- **配置变更通知**：路由/集群增删改、重命名、配置回滚等操作自动产生通知事件
- **告警冷却机制**：同一告警在冷却时间内不重复触发，避免告警风暴
- **事件分发架构**：
  - `IConfigChangeNotifier` 接口：纯事件通知职责
  - `ConfigChangeEventDispatcher`（`BackgroundService`）：队列解耦，3 秒延迟启动，200ms 轮询出队分发
  - `PendingNotification` 值类型（`readonly struct`）入队，零分配压力
- **通知历史记录**：内存环形缓冲区 + SQLite 持久化，支持分页查询和按事件类型过滤
- **通知设置页面**：Webhook 端点管理、告警规则开关、高级设置（超时/重试/冷却/历史上限）
- **默认通知规则种子**：`StartupWarmupService` 启动时自动创建 11 种事件类型的默认规则

#### 健康检查面板
- **集群健康概览页面**（`Views/Dashboard/HealthCheck.cshtml`）：所有集群健康状态总览、目标节点健康评分
- **异常目标节点详情**：展示异常节点地址、状态、失败原因，支持仅显示异常过滤
- **健康检查配置模型**：`ActiveHealthCheckConfig`（主动检查：间隔/超时/策略/路径）、`PassiveHealthCheckConfig`（被动检查：策略/恢复周期）、`ClusterHealthCheckConfig`（组合配置）
- **HealthCheckController API**：`/api/health-check/clusters` 集群健康配置，`/api/health-check/status` 实时健康状态
- **`DefaultHealthCheckService`**：启动时为所有集群自动应用默认被动健康检查策略

#### 熔断器状态面板
- **Dashboard 熔断器查看页**（`Views/Dashboard/CircuitBreaker.cshtml`）：实时展示各集群/节点的熔断状态（Closed/Open/HalfOpen）
- 显示连续失败次数 vs 阈值、恢复超时倒计时、熔断时间、最后访问时间
- 支持一键重置所有熔断器（带确认对话框）

#### 限流状态面板
- **Dashboard 限流查看页**：展示固定窗口、滑动窗口、令牌桶、并发限制四种限流策略的实时状态
- 显示限制数、时间窗口、活跃请求数、已拒绝数、总请求数

#### 设置页面增强
- **数据库下载功能**：在设置页面提供一键下载 SQLite 数据库文件到本地，方便使用数据库工具（DB Browser for SQLite 等）直接查看
- **DashboardController.DownloadDatabase**：解析 `ConnectionString` 中的 `Data Source` 路径，支持相对路径自动转绝对路径，返回 `PhysicalFile` 流式下载

#### SQLite 数据库加密
- 集成 `SQLitePCLRaw.bundle_e_sqlcipher`，支持 SQLCipher AES-256 加密
- 连接字符串添加 `Password=xxx` 即可启用加密（`Data Source=gateway-store.db;Password=MyP@ss`）
- 加密对上层代码完全透明，Repository、下载接口等无需任何改动

#### Agent 自动注册增强
- **HTTP/gRPC 双协议注册**：`GatewayAutoRegistrationClient` 同时支持 HTTP REST API 和 gRPC（`GatewayRegistryClient`）注册
- **指数退避重试**：启动时最多 5 次重试（2s → 4s → 8s → 16s → 30s），提高首次注册成功率
- **自动端口分配**：gRPC 端口 = HTTP 端口 + 1，Kestrel 自动配置 HTTP/1.1 + HTTP/2（h2c）
- **IP 自动解析**：多源检测（Kestrel:EndPoints > Urls > ASPNETCORE_URLS），`KestrelAutoConfigService` 检测 `0.0.0.0` 监听
- **多种认证支持**：Bearer Token、Basic Auth、API Key 三种认证方式
- **智能默认值**：`RegistrationOptionsResolver` 自动推断 RouteName（入口程序集名）、MatchPath（`/{**catch-all}`）、DestinationAddress

#### gRPC 注册接口
- **`Aneiang.Yarp.Grpc` 项目**：`GatewayRegistry.proto` 定义 gRPC 服务契约（`RegisterService`、`UnregisterService`、`Heartbeat`）
- `GatewayRegistryService` 服务端实现：gRPC 注册的路由自动标记来源，支持幂等覆盖注册

---

### 🏗️ 架构重构

#### Storage 模块接口隔离（上帝接口/上帝类消除）
- **`SqliteConnectionFactory`**：共享 SQLite 连接工厂单例，所有仓储复用同一连接池，双检锁确保 `SQLitePCL.Batteries_V2.Init()` 仅初始化一次
- **8 个独立仓储接口**替代原 `IGatewayRepository` 上帝接口：
  - `IRouteRepository`、`IClusterRepository`、`IPolicyRepository`、`IConfigHistoryRepository`
  - `IAuditLogRepository`、`IWafSettingsRepository`、`IProxyLogRepository`、`INotificationRepository`
- **8 个 SQLite 独立仓储实现**：每个仓储仅管理自身表结构，职责单一
- **懒加载建表机制**：`EnsureInitializedAsync()` 双检锁（`lock` + `_initialized` 标记），首次使用时自动建表，不阻塞启动
- **消费方依赖精简**：6 个核心服务从注一整个上帝接口改为只注入需要的 1-2 个子接口
- **DI 统一注册**：`StorageServiceCollectionExtensions.AddAneiangStorage()` 集中注册所有仓储 + 连接工厂

#### 事件架构改进
- **审计与通知职责分离**：`IConfigChangeNotifier`（纯事件）← `IConfigChangeAuditLog`（审计+事件），`ConfigChangeAuditLog` → `ConfigChangeEventDispatcher` 队列分发
- **多级缓存架构**：WAF 设置/动态配置/通知设置 均采用 Python 内存缓存 + `SemaphoreSlim(1,1)` 线程安全双检锁模式

#### 预热/预加载机制
- **`StartupWarmupService`**：应用启动时 `Task.WhenAll` 并行预热 4 大模块（仓库建表、查询缓存、代理日志存储、通知规则种子），秒表计时日志
- **`NotificationWarmupService`**：独立预热通知系统，调用 `PreloadAsync()` 加载渠道/规则/设置到内存缓存
- 错误隔离：每个预热任务静默捕获异常，不影响应用启动

#### 模型和实体层整理
- **实体类拆分为独立文件**：`RouteEntity`、`ClusterEntity`、`DestinationEntity`、`ConfigHistoryEntity`、`PolicyEntity`、`AuditLogEntity`、`ProxyLogEntity`
- **新增模型文件**：`ActiveHealthCheckConfig`、`PassiveHealthCheckConfig`、`ClusterHealthCheckConfig`、`HealthCheckConfig`、`CircuitBreakerConfig`
- **`ConfigEntityMapper`**：静态扩展方法在领域模型与存储实体间转换，统一 `System.Text.Json` camelCase 序列化选项
- **按模块组织模型**：Dashboard 项目的 DTO/模型分散在各 `Modules/*/Models/` 下，按功能边界拆分

#### 配置系统优化
- 统一 `Gateway:{Subsystem}` 命名规范（`Gateway:Storage`、`Gateway:Dashboard`、`Gateway:Dashboard:Waf`）
- `PostConfigure<WafOptions>` 联动：Dashboard 路由前缀自动注入 WAF 选项
- 双重注册模式：关键服务同时注册接口和具体类型（如 `IConfigChangeAuditLog` / `ConfigChangeAuditLog`），均解析为同一单例

---

### 🐛 修复

- **`DynamicYarpConfigService` 线程安全修复**：`ReaderWriterLockSlim`（不支持 await）→ `SemaphoreSlim(1,1)`，解决异步方法内的线程亲和性问题
- **`TrySetRouteDisabled`/`TrySetClusterDisabled` 元数据同步**：修复启用/禁用路由或集群时内存元数据不同步的 bug
- **CS0121 二义性修复**：`ConfigPersistenceService` 中 `EntityMapper` 与新增 `ConfigEntityMapper` 命名冲突，通过显式命名空间调用解决
- **`SqliteConnectionFactory` 命名空间修正**：修复 `StorageOptions` 在特定编译上下文下解析失败的问题

---

### 🔧 移除

- `IGatewayRepository.cs` — 上帝接口（聚合 9 个子接口 + `IAsyncDisposable`）
- `SqliteGatewayRepository.cs` — 970 行上帝类，所有仓储逻辑耦合在同一文件中
- `RedisGatewayRepositoryPlaceholder.cs` — 所有方法均抛 `NotImplementedException` 的死代码
- `IDataStore.cs`、`IStructuredDataStore.cs` — 遗留抽象接口，已被独立仓储取代
- `IDynamicConfigPersistenceService.cs` / `DynamicConfigPersistenceService.cs` — 职责分散到 `IRouteRepository` + `IClusterRepository`
- `DynamicConfigPreloadService.cs` / `WebhookSettingsPreloadService.cs` — 预加载逻辑合并入 `StartupWarmupService`
- `SqliteDataStore.cs`、`RedisDataStore.cs`、`StructuredSqliteStore.cs` — 旧数据存储实现
- `GatewayRepositoryAdapter.cs` — 适配器层，接口隔离后不再需要

---

### 🌐 国际化

新增 120+ 中英文 i18n 键值对，覆盖：
- WAF 防火墙全部 UI（全局开关、检测规则、IP 名单、保存/加载状态）
- 通知告警全部 UI（渠道管理、告警规则、高级设置、事件类型描述）
- 安全事件查看器
- 健康检查面板（集群列表、目标节点、异常详情、状态标签）
- 熔断器面板（状态名称、操作确认、阈值显示）
- 限流面板（策略名称、窗口参数、统计数据）
- 数据库下载（按钮、描述、下载中/完成/失败提示）

---

## [2.3.0.10] - 2026-05-24

### 新增

- 添加审计日志功能，记录所有配置变更操作（操作类型、操作对象、操作者、变更前后 JSON、时间戳）
- 添加 `Aneiang.Yarp.Client` 客户端 SDK，支持微服务一行代码自动注册到网关

### 优化

- 优化配置回滚功能，回滚逻辑更可靠
- 优化日志展示页面交互体验
- 优化代码结构，消除编译警告
- 更新 README 和项目文档

---

## [2.3.0.9] - 2026-05-19

### 新增

- 支持 HTTPS 监听，客户端自动注册支持 HTTPS 地址

---

## [2.3.0.8] - 2026-05-19

### 优化

- 更新 README 文档

---

## [2.3.2.7] - 2026-05-19

### 新增

- 添加 IP 隔离负载均衡策略（`IpBasedLoadBalancingPolicy`），启用后多个开发者共用同一路由路径，网关按请求来源 IP 自动路由，前端完全无感知

---

## [2.3.0.6] - 2026-05-19

### 新增

- 添加 `IWebHostBuilder` 适配，支持传统 ASP.NET Core 宿主模式

---

## [2.3.0.5] - 2026-05-19

### 修复

- 修复客户端自动注册无法正常开启 `0.0.0.0` 监听的问题

---

## [2.3.0.4] - 2026-05-15

### 修复

- 修复 Dashboard 前端资源无法访问的问题

---

## [2.3.0.3] - 2026-05-06

### 新增

- 添加配置导入导出功能，支持标准 YARP 格式互转
- 自动监听 `0.0.0.0`，无需手动配置 Kestrel 地址

### 优化

- 优化代码结构和稳定性

---

## [2.3.0.2] - 2026-04-30

### 新增

- 添加多语言支持（中文/英文运行时切换）

### 修复

- 修复日志滚动条显示问题

### 优化

- 将前端 CDN 资源改为本地加载，离线环境可用
- 优化日志模块性能
- 优化代码结构

---

## [2.3.0] - 2026-04-28

### 新增

- 添加请求日志监控功能，支持实时查看经过网关的请求/响应详情
- 添加日志脱敏和采样机制（Header 黑名单、JSON 字段脱敏、采样率控制）
- 添加 WebSocket 实时日志推送

### 优化

- 优化日志监听中间件

---

## [2.0.0] - 2026-04-28

### 新增

- 初始版本发布
- 基于 YARP 2.3.0 构建
- 可视化 Dashboard 管理面板（RCL 嵌入式架构）
- 动态路由和集群配置管理（运行时 CRUD，无需重启）
- 配置快照与一键回滚
- 原子文件持久化（`gateway-dynamic.json`）
- 四种认证模式（None / DefaultJwt / CustomJwt / ApiKey）
- 路由前缀可配置（`IApplicationModelConvention` 注入）
- `ReaderWriterLockSlim` 线程安全并发控制
- 示例项目（SampleGateway + SampleLocalService）
- MIT 开源协议

---

[GitHub](https://github.com/aneiang/Aneiang.Yarp) | [Gitee](https://gitee.com/aneiangsoft/aneiang-yarp)
