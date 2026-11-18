# 更新日志

## [2.3.0.40] - 2026-11-18

> **统一发布版本**：`2.3.0` 是 YARP 的版本，`40` 是 Aneiang.Yarp 的版本，合起来就是 `2.3.0.40`。2.3.0.30 之后的所有改动都归入本版，不再按里程碑分开发布 —— 原 2.4.0、2.4.1、2.5.0、2.6.0 四批一次交付。下文出现的 2.4.x / 2.5.x / 2.6.x 指开发过程中的里程碑批次，对外版本一律是 `2.3.0.40`；历史小节（2.3.0.30 及更早）保持原版本号不变。

### 📌 本版概览

**多环境配置隔离（原 v2.6 第 1 项，前两批）** —— v2.6 的第一项「多环境配置隔离」拆成三批，本次交付前两批 —— 环境隔离与切换。核心是一条边界：**网关转发哪个环境是启动时定死的，切环境只切"你在编辑哪个"**。这条边界不是设计洁癖 —— 如果切面板会改转发，那"打开 staging 看一眼"就等于把生产流量切走了。批三（环境间 diff + 一键提升 + 审批）未做，见文末进度表。

**灰度与可观测（原 v2.5 六项）** —— v2.5 六项全部交付 —— 插件扩展点、权重灰度、灰度应用与自动回滚、指标告警阈值、OpenTelemetry 追踪、故障注入。核心是一条新策略 `Weighted`：按节点权重**精确**分流，配合 `X-Gateway-Canary` 头可以先把一个真实用户钉到新版本验证，再动权重；在此之上是一台靠指标说话的灰度状态机，观察窗口内错误率超阈值就自己退回去；再往外一层是阈值告警，负责在**没有人盯着灰度**的时候把 5xx 率、P99、熔断开闸叫出来。最外层是链路追踪 —— 告警说"错了多少"，span 说"错在哪一跳"。最后是故障注入：前面这些能力都是"等真故障发生时才验证"，而它可以主动把故障造出来，把重试、熔断、告警、自动回滚一次性演练一遍。

**写路径护栏下沉（原 v2.4.1）** —— 2.4.0 的预检与版本号只覆盖了 Dashboard 表单，而真正危险的写入（客户端自注册、批量导入、历史回滚、AI 助手）全部绕过了它们。本版把护栏下沉到核心库，让所有写路径共用同一条闸门，并补上 2.4.0 承诺但未兑现的三项。

**生产化底座（原 v2.4.0）** —— 不再只是"装得上"，而是"敢上线"。核心是把过去只在请求时才暴露的失败提前到保存时，并让指标出得去。

### 🚀 新增

- **多环境配置隔离**（`Gateway:Storage:Environments`）
  - **每环境一个 SQLite 文件，不是每表加一列**。`prod` 用原连接串（`gateway-store.db`），其余环境派生后缀（`gateway-store.dev.db`）。好处有三个：存量部署升级时**零迁移**，数据还在原处；dev 库损坏不影响 prod（爆炸半径隔离）；不需要动任何表结构、任何 SQL、任何仓储
  - **两个概念，显式拆开**：`RuntimeEnvironment`（启动即固定，决定转发）与 `CurrentEnvironment`（可切换，决定面板读写）。构造时二者相等，切换后不等，此时**发布快照会被拒绝**
  - **发布守卫落在唯一收口点**：`DynamicConfigPublisher.Publish` 开头判断"正在编辑的是不是运行环境"。放这里而不是每个调用点，是因为写入路径有单条 / 批量 / 导入 / 回滚 / 自注册五条，漏一条就等于漏掉一次生产事故
  - **切换时重载工作集，不只是换连接**。工作集是后续每次保存的数据源；不重载就会把上一个环境的全量配置写进新环境 —— 这是反方向的泄漏。读库失败时把视图回滚到切换前，不留下一个半开的环境
  - **环境名白名单**（`A-Za-z0-9_-`）。名字要进文件名，这是唯一能拦住路径穿越的地方，配置期就抛错而不是等到 `Data Source=../../prod.db`
  - **面板查询缓存按环境分键**（`dashboard:routes:query:{env}`）。缓存不带环境名的话，切完 5 秒内看到的还是上一个环境的路由列表 —— 足够让人以为切换没生效，然后对着错误的列表保存
  - Dashboard 顶部新增环境切换器：编辑运行环境时是灰色 + 绿色 `LIVE` 徽标；编辑非运行环境时变琥珀色并写明"仍在转发 prod"。单环境部署时**整个控件不显示**，行为与升级前完全一致
  - 切换确认框显式说明"页面数据会换成该环境的配置，正在转发的流量不受影响，未保存内容会丢失"
  - `<prefix>/api/environments`（读）与 `<prefix>/api/environments/switch`（写）
- **故障注入插件**（`Aneiang.Yarp.Plugin.FaultInjection`）
  - 按路由注入三件事：**延迟**（上下限随机）、**错误状态码**（400–599 + 自定义响应体）、**连接中断**（模拟传输层故障，而不是上游 5xx）
  - **位置是刻意的：它排在代理管线最内侧，紧挨 forwarder**。这样注入的故障对管线外层与真实上游故障无法区分 —— 重试会重试它、熔断会记它、指标记一次 5xx、告警会响、灰度会因此回滚。放到最外层就变成一场什么都没验证的演练，因为被演练的组件根本没看见故障
  - **默认什么都不做**：`enabled` 之外每个字段都关着，只勾了启用框的绑定不会把请求变成 200 —— 否则演练会"看起来通过了"，比没做更糟
  - **触发请求头**：配置 `requiredHeader` 后只有带上该头的请求才被注入，真实流量不受影响。这是"敢在生产演练"的前提
  - **注入比例**：`probability` 支持"10% 的请求失败"，不必为了证明一件事把整条路由打死
  - `abortConnection` 与状态码同时配置时以中断为准（自相矛盾的配置不做猜测）
  - `announce`（默认开）在响应头加 `X-Fault-Injected`，避免演练被误认为真实故障；要绝对真实可以关掉
  - **一键关闭是绑定的启用开关，不是插件的开关**：插件管理器会拒绝"还有绑定的插件"被禁用（提示先解绑），这是为了防止误点丢掉演练配置；关掉绑定则下一个请求立刻不再注入，配置保留，随时再演练
  - 延迟遵守 `RequestAborted`：客户端放弃就停止计时，否则长延迟会攒下一堆只能等超时的任务；延迟上限 2 分钟，防止一个手滑把请求挂住
  - 日志只在每个路由首次注入时打 `Warning`，之后降到 `Debug` —— 100% 演练不该把解释演练的日志淹掉
  - 配置走插件中心的绑定编辑器（JSON Schema 校验 + 中英文案），绑定表单在「插件中心 → 稳定性」
- **OpenTelemetry 追踪**（`Aneiang.Yarp.Infrastructure.Observability` + `Aneiang.Yarp.Dashboard.Modules.Tracing`）
  - **埋点在核心库，导出在 Dashboard**：核心库只 `new ActivitySource("Aneiang.Yarp")`，用的是框架自带的 `System.Diagnostics.DiagnosticSource`，**零新依赖**；OTel SDK、instrumentation、OTLP exporter 全部落在 `Aneiang.Yarp.Dashboard`。不开追踪的网关不该被动背上 exporter 的依赖图，更不该在半夜刷"连不上 collector"
  - **分层 span**：`GET /path`（ASP.NET Core）→ `gateway.forward` → `gateway.plugin <id>` × N → `proxy.forwarder` → `GET`（System.Net.Http）。`gateway.forward` 带 `gateway.route.id` / `gateway.cluster.id` / `gateway.destination.id` / `http.response.status_code` / `gateway.short_circuited`
  - **第三方插件零改动即被追踪**：`TracedProxyPipelineBuilder` 包住 `IReverseProxyApplicationBuilder`，插件拿到的 `Use()` 自动套一层以插件 id 命名的 span，`ConfigureProxyPipeline` 的签名不变
  - **状态语义**：只有 5xx 或转发错误才标 `Error`，4xx 不标（OTel 约定 —— 客户端错误不是网关失败）；`gateway.short_circuited=true` 表示请求被插件拦下（既没有目标节点也没有转发错误），与"上游挂了"明确区分
  - **采样是确定性的**：按 traceId 哈希判定，同一条 trace 永远同一结论，不会记了父 span 丢了子 span；**入站 `traceparent` 的 sampled 标记优先于本地比例** —— 覆盖它会把链路正好在网关处劈成两半
  - 采样率运行期可改（面板滑杆），下一个请求即生效，不需要重启
  - **控制面请求不追踪**：`AddAspNetCoreInstrumentation(options => options.Filter = ...)` 排除 `/{prefix}`、`/_content`、`/metrics`、`/health|/ready|/live`、`/favicon.ico`。追踪页 5 秒轮询否则会把自己填满、把真实流量挤出去；健康检查也不需要一个瀑布图
  - **仅内存模式**：不配 endpoint 也能看瀑布图。`TraceRingBuffer` 三重上限（trace 数 / 每 trace span 数 / 每 span tag 数与 tag 长度），**绝不持有 `Activity` 引用**（那会 pin 住 `HttpContext` 和请求体），span 一结束就拍平成字符串
  - **span 到达顺序是子先父后**，根永远最后到，所以渲染时按 `StartedAt` + 父链深度定层，不假设根已经存在
  - 启动失败不阻断进程：endpoint 不合法或 collector 不可达时只记 `StartError`，网关照常转发，错误显示在追踪页而不是让进程起不来
  - **数字要说实话**：`sampledTraces` / `droppedTraces` 是**采样判定次数**，含被 filter 丢弃的控制面请求（采样发生在 filter 之前），所以会大于实际捕获的链路数 —— 页签文案写作"采样判定 / 丢弃判定"并给出原因，"我有多少条链路"看 `bufferCount`；`clear` 连计数器一起归零，避免出现"0 条 trace / 20 个 span"
  - Dashboard：新增 `/tracing` 页 —— 列表 + 瀑布图 + 点 span 看 tags + 点 traceId 复制 + 自动刷新开关；轮询刷新时已展开的 trace 保持展开
  - API：`GET /api/tracing`、`GET /api/tracing/traces/{traceId}`、`POST /api/tracing/sampling`、`POST /api/tracing/clear`
  - 配置段 `Gateway:Dashboard:Tracing`，默认 `Enabled: false`（缺省不产生任何 span，也不注册 exporter）
- **指标告警阈值**（`Aneiang.Yarp.Dashboard.Modules.Alerting`）
  - 三类规则：`ErrorRate`（窗口内 5xx 占比）、`LatencyP99`（窗口内 P99 延迟）、`CircuitOpen`（范围内的开闸数量），范围可选 `Global` / `Cluster` / `Route`
  - **判定顺序是产品语义**：窗口是否填满 → 样本量是否够 → 是否越线 → 是否持续够久。少任何一步，告警都会退化成噪声源：
    - 一个失败请求就是 100% 错误率，所以 `MinSamples` 以下**不判断**；`LatencyP99` 强制最少 20 个样本，否则"P99"只是"最慢的那一次"
    - 窗口没填满时不拿 15 秒的数据冒充 60 秒的速率，返回 `windowNotReady` 并继续等
    - `ForSeconds` 内的越线只是 `Pending`，不通知；持续够久才升级为 `Firing`
  - **无数据不等于恢复**：窗口内没有流量时返回 `InsufficientData`，正在 `Firing` 的告警**原地保持**，不会因为"上游全挂了、错误计数也停了"而发出"已恢复"
  - **窗口滑动**：每跳留一份计数器快照，取"至少一个窗口之前的最新快照"做差。这样每一跳都能判断，而不是滚动窗口里一簇流量被平均掉；`ObservedSeconds` 如实上报实际覆盖的时长（tick 会越过窗口，面板上写的是 74s 而不是 60s）
  - 通知走现有 7 家渠道（`MetricAlert` 事件）。`Critical` 映射成 `Error` 下发 —— 渠道只认 `Error` / `Warning`，其它字符串会落到"一切正常"的绿色勾上
  - **投递状态可见**：`GET /api/alerts` 返回 `delivery.subscribedPlatforms`，没有平台订阅这个事件时面板顶部直接告警"这些告警只会写进日志"，而不是等真实故障时才发现没人收到
  - **恢复通知**：`Firing → Resolved` 发第二条，带持续时长与峰值；只在 `Pending` 阶段就恢复的不发 —— 没人知道它开始过，也就不该有人收到它结束
  - `RenotifyMinutes` 让持续未恢复的告警按间隔再提醒（一次通知等于一条日志）；静默只关通知，不停测量，静默到期后补一条
  - 规则与历史落盘到 SQLite 同目录的 `metric-alerts.json`；重启时残留的 `Firing` 会关闭并标记 `gatewayRestarted`（原来的计数器基线已经不在了，没法可信地续上）
  - API：`GET /api/alerts`、`POST /api/alerts/rules`、`DELETE /api/alerts/rules/{id}`、`POST /api/alerts/rules/{id}/test`（验证渠道是否真的通）、`POST /api/alerts/incidents/{id}/mute`
  - Dashboard：通知管理页新增「指标告警」页签 —— 进行中的告警在前、规则表在后、最近恢复垫底；阈值输入按指标切换单位（百分比 / 秒 / 个数），避免"在比率字段里填 5"
- **`gateway_route_failures_total{route,cluster}`**：按路由的 5xx 计数。告警引用的数字必须在 `/metrics` 里看得见，否则运维没法反驳一条告警
- **灰度应用 + 自动回滚**（`Aneiang.Yarp.Dashboard.Modules.Canary`）
  - 状态机 `Observing → Paused / Completed / RolledBack / Aborted`，阶梯默认 `10 / 30 / 50 / 100`，每一步先写权重再开观察窗口
  - 默认窗口 120s、错误率阈值 20%、最小样本 50 —— 样本不足只 `Hold` 并延长窗口（最多 3 次），不在 3 个请求上就回滚，否则自动回滚会变成抖动
  - **判定顺序即产品语义**：样本量 → 绝对阈值 → 归因。基线自己也在挂时 `Pause` 而不是 `Rollback`（回滚既救不了火，还把现场销毁了），原因码分别为 `baselineDegraded` / `canaryDegraded`
  - 权重没生效（灰度侧一个请求都没收到）单独诊断成 `NoCanaryTraffic` 并点名 `Weighted`，不伪装成"健康"
  - 窗口内增量用「开窗快照 → 关窗相减」从进程级累计计数器里算，负值钳 0；`TimeProvider` 注入，所以"等两分钟看是否回滚"在测试里是 `FakeClock.Advance()`
  - **回滚精确还原**：开始灰度时记录 `BaselineExplicitWeights`，回滚时对原本没有 `Weight` 键的节点**删除该键**，而不是写死默认值 1；集群策略一并还原成灰度前的值
  - 状态以不可变 record + `with` 迁移，`SemaphoreSlim` 守护全部状态迁移；落盘到 SQLite 同目录的 `canary-rollouts.json`（写临时文件再 `File.Move`）
  - 进程内监督器（`PeriodicTimer`，5s 一跳）。**单实例语义**：指标是进程级的，一个集群三台网关就是三次独立 rollout
  - API：`GET /api/canary/rollouts`、`GET /api/canary/candidates/{clusterId}`，`POST /api/canary/rollouts`、`/rollouts/{id}/promote|resume|abort`。灰度接口故意不接 `X-Config-Version` 守卫 —— 灰度自己会推进版本，接了会让"人工 promote"全部 409
  - Dashboard：集群列表每行一个灰度入口，勾节点、填阶梯/窗口/阈值/最小样本即可开跑；面板 5s 轮询，响应未变化时只更新倒计时不动 DOM，结束的 rollout 保留 30 分钟可手动收起
- **`Weighted` 负载均衡策略**（`Aneiang.Yarp.Services.WeightedLoadBalancingPolicy`）
  - 平滑加权轮询（nginx 式）：每 100 次请求，90/10 就是 90 次与 10 次，且交错分布而不是先连发 90 次
  - 权重取自目标节点 `Metadata.Weight`，缺省值可配（`Weighted.DefaultWeight`，默认 1）；`0` 表示"已注册但不接流量"
  - 全部权重为 0 时回退到第一个可用节点 —— 权重表坏了不该变成 503 风暴
  - 每集群状态加锁维护，目标集合变化（健康状态翻转、配置重载）时自动重置累加器，不会带着旧偏置继续跑
  - 与 `IpBased` 一起进入 `ClusterSchema.json` / `ConfigurationSchema.json` 的枚举
- **按人钉住灰度和节点**：请求头 `X-Gateway-Canary` 填 `Tag` 或目标节点名，命中即绕过权重。头的名字、Tag 的键都可经集群 metadata 覆盖（`Weighted.HeaderName` / `Weighted.TagKey`）
- **按目标分组的指标**：`gateway_destination_requests_total{cluster,destination}`、`gateway_destination_failures_total{cluster,destination}`，以及 `gateway_metrics_dropped_destination_series`。数据来自 `IReverseProxyFeature.ProxiedDestination`，只在真正选出节点后才记账
- **Grafana 面板 +2**：「Canary split by destination」「Canary error rate by destination」，新增 `cluster` 变量；面板总数 13
- **插件宿主扩展点**：`PluginCapability` 增加 `LoadBalancingPolicy` / `Transform`；新接口 `IGatewayPluginHostServices` + `PluginHostServiceExtensions.AddPluginHostServices(...)`，让插件能在容器构建期注册 LB 策略与 Transform provider
- **预检覆盖权重表**：非整数权重、负权重、全部为 0、以及"写了 `Tag` 但策略不是 `Weighted`"都会在保存前给出带字段定位的提示（`Destinations.canary.Metadata.Weight` 级）
- **上游连接池指标**：`UpstreamConnectionMonitor` 用 `MeterListener` 订阅 `System.Net.Http` 内置 Meter，输出 `gateway_upstream_connections{host,state}`、`gateway_upstream_connections_created_total{host}`、`gateway_upstream_metrics_available`
  - 同时处理两种语义：.NET 8 的 `open_connections` 是 UpDownCounter（增量），更新版本是 observable gauge（绝对值），按 `Instrument.IsObservable` 区分
  - 主机数上限 100，超出并入 `host="other"`；`gateway_upstream_metrics_available=0` 明确表示"运行时没有这类指标"，避免把 0 误读为"池是空的"
- **TTFR 指标**：`gateway_serving`（0/1）与 `gateway_first_success_seconds` —— 从进程启动到第一个成功的代理请求。此前"装好了但一条都没转发"这种状态在所有指标上都看不出来。
- **Grafana 模板随包提供**：`content/observability/grafana-dashboard.json`（`contentFiles` 同步一份），面板从 8 个扩到 11 个（TTFR、Serving、上游连接池）
- **CI 流水线**：`.github/workflows/ci.yml`
  - net8.0 / net9.0 双 TFM 矩阵编译 + 跑测试
  - 测试数下限断言（< 100 直接 fail）—— 防止将来过滤条件写错导致"0 个用例也算通过"
  - `dotnet pack` + 包内容检查，抓"仓库里有、包里没有"这类问题
- **Prometheus 指标端点**：`GET /metrics` 输出 Prometheus 文本格式 0.0.4
  - `gateway_requests_total{route,cluster}`、`gateway_request_duration_seconds`（13 桶直方图 + `_sum` + `_count`）、`gateway_responses_total{class}`、`gateway_inflight_requests`、`gateway_uptime_seconds`
  - 由 `GatewayMetricsRenderer` 追加需要 DI 的 gauge：`gateway_build_info`、`gateway_routes_configured`、`gateway_clusters_configured`、`gateway_config_version`、`gateway_circuits{state}`
  - 采集点在反向代理管道内（`GatewayMetricsMiddleware`），**不依赖任何插件是否绑定**；此前仪表盘数据只在 metrics / proxy-log 插件启用时才有
  - 基数上界 `MaxTrackedSeries = 300`，超出并入 `route="other",cluster="other"` 并计入 `gateway_metrics_dropped_series`，防止路由模板写错把 Prometheus 打挂
  - `DashboardUseOptions` 新增 `UseMetricsCollection` / `MapMetricsEndpoint` 开关，默认开启
- **配置预检（dry-run validate）**：`POST /api/config/validate` 返回 `ConfigValidationReport`，每条问题带 `field`（可精确到 `Destinations.d1.Address`）、`message`、`hint`
  - 路由侧：RouteId / ClusterId 为空、Path 与 Hosts 全空、Methods 空数组、Header 缺 Name、Timeout 与 TimeoutPolicy 同时设置、Timeout ≤ 0、集群不存在、四个策略名不可解析、`Metadata.Enabled=false` 的禁用路由
  - 集群侧：无目标节点、地址为空或非 http/https、非内置负载均衡策略、会话亲和缺 AffinityKeyName 或策略未知、健康探测间隔/超时为 0、关闭上游证书校验、HTTP 版本越界
- **全量配置体检**：`GET /api/config/integrity` 交叉检查指向不存在集群的路由、无目标节点的集群、无人引用的孤儿集群、重复的 `Match.Path`（后者只有优先级最高的一条会命中，其余是死路由）
- **策略名可用性校验 + 9 个内置策略**：`GET /api/config/policies`
  - 授权 / CORS / 限流 / 输出缓存四个字段由自由文本改为下拉（`dashboard-form-builder.js` + `data-kind="policy"`）
  - 内置 `gw-anonymous`、`gw-authenticated`、`gw-cors-open`、`gw-rl-100pm`、`gw-rl-1000pm`、`gw-rl-10ps`、`gw-cache-30s`、`gw-cache-5m`、`gw-cache-1h`
  - 授权走 `IAuthorizationPolicyProvider`（权威：null 即未注册），CORS 走 `ICorsPolicyProvider`；限流与输出缓存因 `PolicyMap`/`Policies` 是 internal，返回 `Unverifiable` 而非谎报 `Unknown`，避免误杀宿主自定义策略
  - **补上 `app.UseRateLimiter()`** —— 此前策略即使注册也永不下发，等于换个地方继续静默失败
  - 下拉支持"自定义名称…"（`window.prompt`），未注册的名字保存时被拒绝并标注
- **变更差异预览**：保存前用自然语言列出变化（`删除目标节点 d1：http://…` / `匹配路径：/a → /b` / `策略 RateLimiterPolicy：不启用 → gw-rl-100pm`），含"删除"的变更额外二次确认；差异同时写入配置历史的 summary
- **配置版本号 + 乐观并发**：`GET /api/config/state` 返回 `{ version, lastModified, routes, clusters }`；所有写操作读取 `X-Config-Version`（或 `expectedVersion` 查询参数），版本不一致返回 `409` 与 `currentVersion`；`dashboard-api.js` 自动注入并在写成功后回读

### 🔧 架构改进

- 新增 `Modules/GatewayConfig/Services/ConfigurationValidator.cs`（`IConfigurationValidator`），写路径统一为"校验通过才落盘"
- 新增 `Infrastructure/Policies/GatewayPolicyCatalog.cs`，`AddGatewayPolicies()` 追加式注册（与宿主已有的 `AddAuthorization` / `AddCors` 共存），全部 `TryAdd`/`AddXxx(options)` 语义
- 新增 `Infrastructure/Observability/`：`GatewayMetricsCollector`（无 DI 依赖、线程安全、有界基数）、`GatewayMetricsMiddleware`、`GatewayMetricsRenderer`
- `ConfigControllerBase` 新增 `TryRejectStaleWrite()` 与 `HasDestinations()`（后者避免"首次建集群 + 建路由"时误报集群不存在）

### 🐛 修复

- **连接串为空时所有环境会共用同一个库文件**（写这个特性时被测试抓出来）。原本空串直接 `return "Data Source=gateway-store.db"`，于是 dev 与 prod 指向同一文件，隔离形同虚设 —— 而这种配置在实际部署里很常见（只写了 `Provider` 没写连接串）。改为先补默认串再走派生
- **面板的路由 / 集群列表跨环境串味**（端到端撞出来）。两个查询服务把缓存键硬编码成 `dashboard:routes:query`，切环境后 5 秒内仍返回上一个环境的内容。现在键里带环境名，并且 `InvalidateQueryCaches()` 改成向服务要失效，不再按硬编码键名删
- **重试耗尽后请求变成未处理异常，真实 5xx 被吞掉（严重，由故障注入演练暴露）**。`RequestRetryMiddleware` 在把缓冲响应拷回客户端**之后**才写 `X-Retry-Count`：
  ```csharp
  await responseStream.CopyToAsync(originalResponseBody, context.RequestAborted);
  context.Response.Headers["X-Retry-Count"] = attempt.ToString();  // 此时响应已启动
  ```
  `CopyToAsync` 一写就把响应启动了，随后设置响应头抛 `InvalidOperationException: Headers are read-only, response has already started`。结果是**只要重试次数耗尽**（也就是最需要看清上游回了什么的时候），客户端拿到的是一个 500 加一条未处理异常，而真正的 503 / 502 完全丢失。已把写头移到拷贝之前。这个 bug 存在以来一直没被发现，因为要触发它需要"路由配了重试"且"上游连续失败到用尽重试"，正常流量里几乎不会同时出现 —— 是注入 503 的演练把它撞出来的
- **权重热更新不生效：写了权重，流量照旧（严重）**。`WeightedLoadBalancingPolicy` 原先用 `DestinationState` 的引用判断"目标集合是否变了、要不要重算权重表"。但 YARP 重载配置时**保留** `DestinationState` 实例、只替换它的 `Model`：
  ```csharp
  // ProxyConfigManager.UpdateRuntimeDestinations
  if (currentDestination.Model.HasChanged(incomingDestination.Value))
      currentDestination.Model = new DestinationModel(incomingDestination.Value);
  ```
  于是那个引用判断永远为 false，权重表在集群第一次收到请求后就被**永久冻结**：把 90/10 改成 0/100，实测仍是 50/50，而且没有任何日志、没有任何报错。灰度每一步改的都是权重，这个 bug 会让整个灰度功能"看起来在跑、实际一步都没动"。现在判断依据改成 `DestinationModel` 引用 + destination id + `weightKey` + `defaultWeight`，权重在下一个请求就生效
- **集群接口不再返回 `"Default"` 这个不存在的策略名**。此前没有显式配置策略的集群会被返回 `LoadBalancingPolicy: "Default"`，而 JSON 编辑器会把它原样写回 —— 于是"只改一个权重再保存"就会把集群改成一个 YARP 解析不到的策略。现在返回空串，编辑器不再写回该字段
- **插件在错误的位置注册扩展点会被无声丢弃的问题，改成显式契约**：`IGatewayPlugin.ConfigureServices` 跑在插件自己的运行时容器里，那里注册的 `ILoadBalancingPolicy` YARP 永远看不见；新增的 `IGatewayPluginHostServices` 才是正确入口，缺少无参构造时启动即抛错而不是静默跳过
- **四条机器写入路径现在都受同一个守卫管辖**：
  | 写入路径 | 2.4.0 | 2.4.1 |
  |:--|:--:|:--:|
  | Dashboard 路由 / 集群 CRUD | ✅ | ✅ |
  | 客户端自注册 `POST api/gateway/register-route` | ❌ | ✅ |
  | 批量导入 `ImportFullConfigAsync` | ❌ | ✅ |
  | 历史回滚 `RollbackAsync` | ❌ | ✅ |
  | AI 助手 `create_route` | ❌ | ✅ |
- 新增核心库抽象 `Aneiang.Yarp.Validation.IConfigurationGuard`（`ConfigurationGuardResult` / `ConfigurationGuardIssue`），默认实现 `PermissiveConfigurationGuard` 全部放行 —— **只引核心库不引 Dashboard 的部署行为完全不变**；Dashboard 通过 `services.Replace(...)` 换成 `ConfigurationValidatorGuard`。
- 批量校验新增 `IConfigurationValidator.ValidateBatchAsync(routes, clusters)`：把"本批次要创建的集群"算作已存在，避免导入/回滚时整批误报"集群不存在"；错误字段带归属前缀（`Clusters.c1.Destinations.d1.Address`），30 个集群同时出错时能定位。
- `ConfigConcurrency` 统一了版本头的解析与 409 载荷，核心控制器与 Dashboard 共用一份实现。
- **回滚前预检**：快照可能是几个月前的，指向已改名的集群或已删除的策略。校验不通过直接拒绝回滚并记 warning —— "恢复配置"把故障放大是最坏的情况。
- 移除全站 10 处静态说明条（集群/路由/插件/插件资源/日志/熔断/设置/通知/概览等页），同时清理 `.page-help-bar` 相关 CSS 与 10 个 i18n 死键，避免页面跳转后又冒出一条
- 保留带状态与操作的提示条（配置应用失败告警 `config-alert-bar`、`ov-proxy-alert`、数据来源诊断 `ds-host`）

### 📝 文档

- `README.md` / `README.zh-CN.md` 新增「Multi-Environment （2.3.0.40）」/「多环境（2.3.0.40）」：运行时与编辑视角的区别、文件布局、切换用法与代价、升级零迁移的保证
- `samples/SampleGateway/appsettings.examples/appsettings.MultiEnvironment.json`：可直接用的三环境配置
- 路线图 §6 v2.6 第 1 项标记为分批交付，写明已完成与未完成的部分
- `README.md` / `README.zh-CN.md` 新增「Metric Alerts （2.3.0.40）」/「指标告警（2.3.0.40）」，含规则示例、判定顺序、无数据语义与配置块
- `README.md` / `README.zh-CN.md` 新增「Distributed Tracing （2.3.0.40）」/「链路追踪（2.3.0.40）」，含 span 层级、采样语义、控制面过滤、仅内存模式与配置块
- `README.md` / `README.zh-CN.md` 新增「Canary Releases （2.3.0.40）」/「灰度发布（2.3.0.40）」，含完整配置示例与钉住用法
- `README.md` / `README.zh-CN.md` 新增「Fault Injection （2.3.0.40）」/「故障注入（2.3.0.40）」，含配置块、触发请求头用法、「一键关闭指的是绑定」、注入点位于重试内侧、`announce` 与 `X-Fault-Injected` 语义
- 两处 README 补「Automatic ramp and rollback」/「自动放量与自动回滚」：状态机用法、判定顺序为什么是这个顺序、精确回滚、进程内语义、`Aneiang:Yarp:Dashboard:Canary` 配置块
- `docs/adr/ADR-002-open-lb-and-transform.md` 补充「实施记录」：原决策假设 `ConfigureServices` 可用于容器构建期注册，实施时核实该假设不成立，因此改成独立接口
- 路线图 §0.1 / §3 / §4.5 / §6 v2.5 按实际状态更新
- **README 增加「我们明确不做的事」**（中英同步，6 条非目标 + 理由 + 替代方案）：API 门户、自带 IdP、日志存储与检索、Service Mesh 控制面、K8s Ingress Controller、分布式限流
- **三个 ADR 落地** `docs/adr/`：ADR-001 单控制面、ADR-002 开放 LB/Transform 扩展点、ADR-003 限流只留扩展位
- 路线图 `docs/product/yarp-product-roadmap.md` 逐项标记交付状态

### ✅ 测试

- 用量 387 个（2.5.0 为 358）：新增 `GatewayEnvironmentRegistryTests`（13 个）、`SqliteEnvironmentConnectionStringTests`（11 个）、`EnvironmentIsolationIntegrationTests`（4 个，真实容器 + 真实 SQLite 文件）
- `SqliteEnvironmentConnectionStringTests` 钉的是几条会被想当然的地方：运行环境**原样返回**配置串（这是零迁移的根据）、后缀加在扩展名之前（`store.dev.sqlite` 而不是 `store.sqlite.dev`）、SQLCipher 的 `Password` 与其他参数在派生后仍然保留（丢了就只有切过去时才发现打不开）、`:memory:` 与不可解析串不做猜测
- `GatewayEnvironmentRegistryTests` 钉住 `Runtime` 永远在列表里（否则切走就切不回来）、大小写不敏感去重、以及 7 种可能逃出库目录的名字必须被拒绝
- `EnvironmentIsolationIntegrationTests` 是这批里最重要的一组：它不信任何单点，而是把容器、库、工作集、已发布快照串起来跑。其中 `Switching_to_a_non_runtime_environment_does_not_publish_or_forward_its_config` **在临时停用发布守卫后确实变红**（1 failed / 3 passed），确认它不是靠巧合绿着
- 另跑了一遍真实网关（SampleGateway + 两个真实 SQLite 文件，11 项断言全过）：`api/environments` 报 `runtime=prod multi=true`；prod 建 `prod-only` → 切 dev（`published=false`，列表为空）→ dev 建 `dev-only` → `prod.db` 与 `prod.dev.db` 两个文件同时存在 → 切回 prod（`published=true`）→ 列表回到 `prod-only`，`dev-only` 未出现；`../prod`、`..`、`pr od`、`prod.db`、`nope` 五种非法名全部 400；全程无未处理异常
- `dotnet build Aneiang.Yarp.sln` 0 警告 0 错误
- 用量 358 个（2.4.1 为 158）：新增 `WeightedLoadBalancingPolicyTests`（25 个，含 90/10、99/1、并发 400 次仍精确、表变更重置、**权重在同一个 `DestinationState` 上被就地替换后仍然生效**）、`PluginHostServiceTests`（12 个）、`CanaryRolloutEvaluatorTests`（9 个）、`CanaryWeightPlannerTests`（10 个）、`CanaryRolloutServiceTests`（11 个，`FakeClock` 驱动）、`MetricAlertEvaluatorTests` + `MetricAlertRuleValidatorTests` + `GatewayMetricsPercentileTests`（31 个）、`MetricAlertServiceTests`（16 个，`FakeClock` 驱动）、`GatewayTracingTests`（7 个）、`TraceRingBufferTests`（9 个）、`TracingSamplerAndServiceTests`（10 个）、`FaultInjectionPlannerTests`（20 个）、`FaultInjectionMiddlewareTests`（11 个）、`RequestRetryResponseTests`（2 个），`ConfigurationValidatorTests` 补 8 个权重用例
- `MetricAlertServiceTests` 钉住的是语义而不只是数字：`Pending` 阶段恢复不发通知、静默期间数值继续更新、持续未恢复按间隔重复提醒、无流量时不判定恢复、重启后残留告警关闭
- `GatewayTracingTests` / `TraceRingBufferTests` / `TracingSamplerAndServiceTests`（26 个）钉住的是几条"看起来对但会骗人"的语义：span 子先父后到达仍能拼出完整 trace、**根不在缓冲区时按父链深度选根而不是按 `ParentSpanId is null`**、`ShortCircuited` 必须区分"插件拦下"与"上游失败"、被丢弃采样（`!Recorded`）的 span 不入缓冲、非法 endpoint 只记 `StartError` 不抛；采样侧钉住 ratio 0/1/0.5 的分布（400 次落在 140~260）、同一 traceId 结论一致、**入站 sampled 标记覆盖本地比例**
- 追踪另跑了一遍真实网关（SampleGateway + 假 OTLP 接收端，非单测）：`/api/tracing/traces/{id}` 返回 10 层嵌套瀑布图；`:4318` 收到 1 个 http/protobuf 请求，内含 `gateway.forward` / `gateway.plugin`×N / `proxy.forwarder` / `service.name`；禁用态 `enabled=false running=false buffer=0` 且不产出任何 span；`ratio=0` 后连发 3 个请求缓冲区不增长；未知 traceId 返回 404
- 新增 `LoadBalancingIntegrationTests`（6 个）：真实 Kestrel 代理 + 两个真实 Kestrel 后端，覆盖"启动时就带策略"和"运行中改策略 / 改权重"两条路径。上一条那个 bug 单测抓不到，因为单测每次都会新建 `DestinationState`；只有跑在真 YARP 上才会露出"实例被复用"
- `FaultInjectionPlannerTests`（20 个）钉的是**概率边界而不是统计分布**：`sample` 由调用方传入，所以 0.5 的边界可以精确断言（`0.49` 命中、`0.5` 不命中），不用发 1000 个请求看比例像不像；另包含"启用了但一条规则都没配"必须什么都不做、"延迟上下限写反了按小的那个算"、"只要 400–599 之外的码"、"`abortConnection` 与状态码冲突时以中断为准"
- `FaultInjectionMiddlewareTests`（11 个）钉的是**中间件层的行为契约**：注入时不调 `Next`、自定义 body 走 `text/plain`、`announce` 关掉后没有 `X-Fault-Injected`、只加延迟时仍然转发（实测耗时 ≥60ms）、abort 走 `IHttpRequestLifetimeFeature.Abort`、请求头门放行与拦截两侧、以及 `/dashboard/plugins` 永不被注入（控制面不该被自己的演练打死）
- `RequestRetryResponseTests`（2 个）钉住上面那个重试 bug：用一个 `HeaderProbeStream` 在**第一次 Write 时**检查 `X-Retry-Count` 是否已经存在 —— 换句话说，测的不是"最终有响应头"，而是"写在响应启动之前"。修复前该用例确实是红的（`1 failed, 1 passed`），修复后转绿
- 故障注入另跑了一遍真实网关（SampleGateway + 真实上游，非单测，11 项断言全过）：插件被自动发现（列表 11 个）、无绑定时 200、绑 `errorStatusCode:503` 后返回 503 且带 `X-Fault-Injected: 503`、请求头门无头 200 / 带头 503、`delayMinMs:400` 实测 `0.445s`、`gateway_route_failures_total{route="baidu_api_route"} 2` 能看见注入的失败、**关掉绑定后不仅状态码回 200、耗时也回到 `0.028s`**（证明延迟确实不再注入，而不只是码变了）、重新启用后配置仍在（`0.441s`）、删除绑定回到 200、全程 `fatal-lines=0` 无未处理异常
- `dotnet build Aneiang.Yarp.sln` 0 警告 0 错误
- 158 个用例全绿（2.4.0 为 117），新增：批量校验 7 例、并发契约 15 例、守卫适配器 4 例、TTFR 8 例、连接池监控 7 例

---

- 新增 `tests/Aneiang.Yarp.UnitTests`（xUnit，net9.0，**117 个用例**，全绿）
  - `YarpJsonConfigTests`：空串/空白布尔、`"true"`/`"True"`、`""` 与 `00:00:30`/`30`/`30s`/`5m`/`2h`/`1d` 的 TimeSpan 解析、裸数字必须是秒而非天、空 JSON 不抛、非法 JSON 抛、SessionAffinity 往返
  - `ConfigurationValidatorTests`：路由/集群/全量体检共 40+ 断言点，含"同一 payload 内新建集群不误报"
  - `GatewayMetricsCollectorTests`：直方图累积性、`_sum` 单位为秒、负耗时钳制、标签转义（引号/反斜杠/换行）、基数上界与 overflow
  - `GatewayPolicyCatalogTests`：9 个内置策略全部可解析、大小写敏感、`Unverifiable` 语义、`AddGatewayPolicies` 与宿主 `AddCors` 共存
- 测试工程已加入 `Aneiang.Yarp.sln`

### 📊 交付进度

| 批次 | 内容 | 状态 |
|:--:|:--|:--|
| 1a | 环境模型 + 每环境独立存储 + 运行时/编辑视角分离 + 切换 | ✅ **2.6.0** |
| 1b | 环境间 diff（路由/集群/绑定逐项差异） | ⬜ 未开始 |
| 1c | 一键提升 + 审批（依赖 1b 与 RBAC） | ⬜ 未开始 |

---

| # | 项 | 状态 |
|:--:|:--|:--|
| 1 | 插件扩展点（LB 策略 / Transform） | ✅ **2.5.0**：`IGatewayPluginHostServices` + `AddPluginHostServices` |
| 2 | 权重灰度（`Weighted` 策略 + `X-Gateway-Canary` 钉住） | ✅ **2.5.0**：精确分流 + 热更新 |
| 3 | 灰度应用 + 自动回滚（观察窗口 + 错误率阈值） | ✅ **2.5.0**：状态机 + 归因判定 + 精确回滚 + Dashboard 面板 |
| 4 | 指标告警阈值（5xx 率 / P99 / 熔断开闸） | ✅ **2.5.0**：三类规则 + 滑动窗口 + 持续判定 + 无数据保护 + 投递状态可见 |
| 5 | OpenTelemetry 追踪 | ✅ **2.5.0**：分层 span + OTLP 导出 + 确定性采样 + 仅内存瀑布图 |
| 6 | 故障注入插件 | ✅ **2.5.0**：延迟 / 错误码 / 连接中断 + 请求头门 + 一键关绑定 |

---

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
- GitHub Issues: https://github.com/aneiangsoft/Aneiang.Yarp/issues
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

### � gRPC 注册通道完善（Aneiang.Yarp.Grpc / Aneiang.Yarp.Client）

- **客户端补齐 Phase 2 注册能力**：`GatewayRegistrationOptions` 新增 `MatchPaths`（多路径 → 多路由共享集群）、`LoadBalancingPolicy`（PowerOfTwoChoices/Random/RoundRobin/LeastRequests）、`ExtraDestinationAddresses`（额外目标全部加入集群负载均衡）、`Metadata`（自定义元数据）；gRPC 注册请求完整传递上述字段（此前仅发送单路径/单目标，LB 策略与 metadata 被丢弃）
- **客户端 SDK 公开查询/更新 API**：新增 `GetServicesAsync`（查询网关已注册服务）与 `UpdateDestinationsAsync`（整体替换集群目标集）公开方法（gRPC 模式专用）
- **心跳失败自动恢复**：客户端心跳连续失败 3 次后自动触发重新注册（覆盖网关重启丢失动态注册状态场景），成功后心跳继续；gRPC 心跳异常补齐日志
- **gRPC 注销多路径残留修复**：多路径注册生成的 `{serviceId}-path{N}` 路由此前注销时全部残留；现按服务 ID 精确匹配（含 path 后缀校验）逐一移除
- **gRPC 心跳兼容多路径**：心跳按 `{serviceId}` 解析不到路由时回退解析 `{serviceId}-path{N}`，多路径注册的心跳不再失效
- **UpdateDestinations 保留 LB 策略**：gRPC 更新目标集时保留集群已有 LoadBalancingPolicy（此前 `TryAddCluster` 整体覆盖会静默重置为 null）


### �📝 补充说明

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

```
src/Aneiang.Yarp.Dashboard/
├── Infrastructure/
│   ├── Deployment/
│   │   ├── DeploymentOptions.cs        # 配置选项 + 5 种枚举
│   │   ├── EndpointRoleResolver.cs     # 端口→角色映射
│   │   ├── DeploymentConfigValidator.cs # 启动验证
│   │   ├── ConfigSnapshot.cs           # 快照模型 + 文件存储
│   │   └── DeploymentCli.cs            # 命令行解析
│   ├── Middleware/
│   │   ├── EndpointRouterMiddleware.cs # 端口路由分发
│   │   └── HealthCheckMiddleware.cs    # 健康检查
│   ├── HostedServices/
│   │   ├── ConfigurationFileWatcher.cs # 文件热更新
│   │   └── KestrelEndpointChangeDetector.cs # 端点变更检测
│   └── Alert/
│       ├── IGatewayAlertService.cs     # 告警接口
│       └── NullGatewayAlertService.cs  # 默认实现
├── Infrastructure/Auth/
│   └── TotpHelper.cs                   # TOTP 2FA 验证器
├── Modules/Dashboard/
│   ├── Controllers/DeploymentInfoController.cs # /api/deployment/* 接口
│   └── Views/Dashboard/Deployment.cshtml         # 运行模式展示页
└── wwwroot/js/modules/
    └── dashboard-deployment.js         # 前端模块

samples/SampleGateway/appsettings.examples/
├── appsettings.AllInOne.json           # 兼容模式示例
├── appsettings.Split.json              # 双端口拆分示例
├── appsettings.SplitWithHealth.json    # 含健康检查示例
├── appsettings.ProxyOnly.json          # 仅代理示例
└── README.md                           # 使用说明
```

### 📝 使用示例

```csharp
// Program.cs 一行启用
builder.Services.AddAneiangYarpDeployment(builder.Configuration);

// 中间件挂载（在 UseAneiangYarpDashboard 之前）
app.UseMiddleware<EndpointRouterMiddleware>();
app.UseMiddleware<HealthCheckMiddleware>();
app.UseAneiangYarpDashboard();
```

```json
// appsettings.json 双端口配置
{
  "Kestrel": {
    "Endpoints": {
      "Proxy":     { "Url": "http://0.0.0.0:80" },
      "Dashboard": { "Url": "http://127.0.0.1:5000" }
    }
  },
  "Gateway": {
    "Deployment": {
      "Mode": "Split",
      "EndpointRoles": { "Proxy": "Proxy", "Dashboard": "Dashboard" }
    }
  }
}
```

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

[GitHub](https://github.com/AneiangSoft/Aneiang.Yarp) | [Gitee](https://gitee.com/aneiangsoft/aneiang-yarp)
