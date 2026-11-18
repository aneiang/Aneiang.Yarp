<div align="center">
<img src="cover-zh.png" alt="Aneiang.Yarp — 为 .NET 而生的反向代理与 API 网关" width="100%"/>

<br/>

<img src="logo.png" alt="Aneiang.Yarp" width="240" style="border-radius: 15px;"/>

**Aneiang.Yarp — 基于 YARP 的全功能 API 网关**

插件体系 · Dashboard · 动态路由 · WAF 防火墙 · 服务发现 · AI 助手 · 2FA 验证 · IP 隔离

[![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.svg)](https://www.nuget.org/packages/Aneiang.Yarp)
[![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.Dashboard.svg)](https://www.nuget.org/packages/Aneiang.Yarp.Dashboard)
[![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.Client.svg)](https://www.nuget.org/packages/Aneiang.Yarp.Client)
[![YARP](https://img.shields.io/badge/YARP-2.3.0-blue.svg)](https://github.com/microsoft/reverse-proxy)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0-purple.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

[English](README.md) | [中文](README.zh-CN.md)

</div>

---

**Aneiang.Yarp** 是基于 [微软 YARP](https://microsoft.github.io/reverse-proxy/) 2.3.0 构建的生产级 API 网关。它开箱即用地提供了你原本需要自行搭建的全部能力：**插件化架构**（10 个内置插件）、可视化**管理面板**、WAF 防火墙、AI 智能助手、多 Provider **服务发现**、通知告警、健康检查与熔断监控，以及微服务**一行代码自动注册**。

[📖 文档](https://yarp.aneiang.com/docs/index.html#overview) · [🚀 在线演示](https://yarp-test.aneiang.com/aneiang) `admin` / `demo123` 

---

## Aneiang.Yarp 与裸 YARP 对比

| 需求 | 裸 YARP | Aneiang.Yarp |
|:-----|:----------|:-------------|
| 管理界面 | 自行搭建 | ✅ 内置 |
| 运行时编辑配置 | 仅支持重载文件 | ✅ 增删改查 + 表单/JSON 编辑 + 回滚 + 审计 |
| 安全防护（WAF） | 无 | ✅ WAF 插件（IP 黑白名单、SQLi、XSS、路径遍历） |
| 微服务注册 | 无 | ✅ 一行代码 REST/gRPC + 心跳自愈 |
| 可观测性 | 仅日志 | ✅ 指标、请求日志、健康检查、熔断面板 |
| 服务发现 | 手动维护集群 | ✅ Consul / Nacos / Eureka / K8s / HTTP-JSON / Static |
| AI 助手 | 无 | ✅ 对话式管理（40 个工具，读写分离） |
| 扩展性 | 自定义中间件 | ✅ 即插即用的 `IGatewayPlugin` 插件框架 |

---

## 系统架构

```
                   +-----------------------------------------------------------+
                   |                  Aneiang.Yarp 网关（YARP 2.3.0）              |
 +-------------+   |   +------------------+  +-------------------------------+  |
 |  5+ 个客户端  |   |   |  Web Dashboard   |  |  管理 API + gRPC 宿主             |  |
 |   浏览器/App  |   |   |  路由 / 插件       |  |  客户端自动注册                   |  |
 +-------------+   |   |  WAF / AI / 2FA   |  |                               |  |
                   |   +--------+----------+  +--------------+----------------+  |
                   |            +------------------------------------------------+ |
                   |            |                                                  |
                   |   +--------v---------+                                    | |
                   |   |  插件管道（10 个内置插件）                                    | |
                   |   |  WAF → 限流 → 重试 → 熔断 →            | |
                   |   |  压缩 → 缓存 → 指标 → 日志              | |
                   |   +--------+---------+                                    | |
                   |            +------------------------------------------------+ |
                   |            |                                                  |
                   |   +--------v---------+   +-------------------------------+  |
                   |   |  YARP 反向代理       | → |  目标服务 / 集群               |  |
                   |   |   动态路由         |   |  （通过服务发现）              |  |
                   |   +--------+---------+   +-------------------------------+  |
                   |            |
                   |   +--------v--------+
                   |   |  存储：SQLite（SQLCipher AES-256） |
                   +-----------------------------------------------------------+
```

---

## 项目亮点

- **插件化架构** —— 一切都是插件；11 个第一方插件提供 WAF、限流、重试、熔断、压缩、缓存、监控等能力。编写自己的 `IGatewayPlugin` 即可在几分钟内扩展网关。
- **可视化管理面板** —— 在一个界面统一管理集群、路由、插件、WAF、健康检查、熔断器、日志、通知、审计与配置历史。
- **运行时动态路由** —— 无需重启即可创建 / 编辑集群与路由，自动持久化并在重启后自动恢复。
- **原生 YARP 配置编辑** —— 表单 + JSON 双模编辑器，直接面向标准 YARP Schema。
- **多 Provider 服务发现** —— Consul、Nacos、Eureka、Kubernetes、HTTP-JSON、Static。
- **客户端一行注册** —— 微服务启动自动注册、关闭自动注销，支持 REST / gRPC + 心跳自愈。
- **AI 智能助手** —— 以对话方式管理网关；40 个 Function Calling 工具，读写权限严格分离。
- **灵活部署** —— `Auto` / `AllInOne` / `Split` / `ProxyOnly` / `DashboardOnly`，基于角色的端口路由。

---

## 快速开始

### 最小可用版

```bash
dotnet add package Aneiang.Yarp
dotnet add package Aneiang.Yarp.Dashboard
dotnet add package Aneiang.Yarp.Storage.Sqlite
```

```csharp
// Program.cs —— 网关
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAneiangYarp();
builder.Services.AddAneiangStorage();
builder.Services.AddAneiangYarpDashboard();

var app = builder.Build();
app.UseAneiangYarpDashboard();   // 内部已包含 MapReverseProxy
app.Run();
```

Dashboard 地址：`http://localhost:5000/apigateway`

**微服务** —— 启动自动注册，无需额外代码：

```bash
dotnet add package Aneiang.Yarp.Client
```

```csharp
builder.Services.AddAneiangYarpClient();
```

```json
{ "Gateway": { "Registration": { "GatewayUrl": "http://localhost:5000" } } }
```

### 推荐版（多端口 + gRPC + 热更新）

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.UseYarpKestrelAutoConfig();           // 多端口 / gRPC 支持
builder.Services.AddAneiangYarp();
builder.Services.AddAneiangStorage();
builder.Services.AddAneiangYarpDashboard();
builder.Services.AddAneiangYarpDeployment();  // 部署模式 + 健康端点

var app = builder.Build();
app.UseAneiangYarpDashboard();
app.Run();
```

> 详细分步指南：[快速开始](https://yarp.aneiang.com/docs/index.html#quickstart)

---

## 包结构

| 包 | 用途 | 依赖 YARP |
|:----|:-----|:---:|
| **Aneiang.Yarp** | 网关核心：动态路由、IP 隔离、API 鉴权、插件执行 | ✅ |
| **Aneiang.Yarp.Dashboard** | Web 管理面板 + AI 助手 | 通过核心库 |
| **Aneiang.Yarp.Client** | 一行代码自动注册（无 YARP 依赖） | ❌ |
| **Aneiang.Yarp.Storage.Sqlite** | SQLite 存储（SQLCipher AES-256 加密） | 通过存储层 |
| **Aneiang.Yarp.Storage.Abstractions** | 存储接口与实体 | ❌ |
| **Aneiang.Yarp.Grpc** | gRPC 注册协议 | ❌ |
| **Aneiang.Yarp.Plugin.Abstractions** | 插件契约（`IGatewayPlugin`） | ❌ |
| **Aneiang.Yarp.Plugin.\*** | 11 个第一方插件，按需单独引入 | 通过核心库 |

**.NET 8.0 / 9.0**。

---

## 功能总览

| 领域 | 你能获得 | 详情 |
|:-----|:---------|:-----|
| **路由** | 动态集群与路由、拖拽排序优先级、表单/JSON 双编辑器、导入导出、快照回滚、审计 | [:link:](https://yarp.aneiang.com/docs/index.html#dynamic-routing) |
| **认证鉴权** | `None` / `DefaultJwt` / `CustomJwt` / `ApiKey` / 自定义委托；TOTP **2FA** | [:link:](https://yarp.aneiang.com/docs/index.html#authentication) |
| **安全防护** | WAF：IP 黑白名单、SQLi / XSS / 路径遍历检测、请求限制、安全响应头 | [:link:](https://yarp.aneiang.com/docs/index.html#waf-firewall) |
| **可观测性** | 请求日志（TraceID 配对、脱敏、采样）、WebSocket 实时流、OpenTelemetry 链路追踪（瀑布图 + 导出 Jaeger/Tempo） | [:link:](https://yarp.aneiang.com/docs/index.html#request-logs) |
| **监控告警** | 请求/延迟/状态码指标（P50/P90/P99）、健康检查、熔断面板 | [:link:](https://yarp.aneiang.com/docs/index.html#health-check) |
| **通知** | Webhook 渠道（钉钉 / HTTP）、事件规则 + 冷却、发送历史 | [:link:](https://yarp.aneiang.com/docs/index.html#notifications) |
| **AI 助手** | 对话式管理、40 个工具、读写权限分离、流式输出、多服务商 | [:link:](https://yarp.aneiang.com/docs/index.html#ai-assistant) |
| **IP 隔离** | 按客户端 IP 将请求路由到指定开发者实例，便于协作调试 | [:link:](https://yarp.aneiang.com/docs/index.html#ip-isolation) |
| **客户端 SDK** | 自动注册/注销、心跳自愈、`GetServicesAsync` / `UpdateDestinationsAsync` | [:link:](https://yarp.aneiang.com/docs/index.html#client-registration) |
| **扩展性** | `IGatewayPlugin` 契约、插件中心管理、热重配的配置绑定 | [:link:](https://yarp.aneiang.com/docs/index.html#plugin-system) |
| **混沌演练** | 按路由注入延迟 / 错误码 / 连接中断，请求头门 + 比例控制，关绑定即停 | [:link:](https://yarp.aneiang.com/docs/index.html#plugin-system) |
| **多环境** | dev / staging / prod 配置隔离，每环境独立库文件；切换只切编辑视角，转发不动 | [:link:](https://yarp.aneiang.com/docs/index.html#plugin-system) |
| **配置安全** | 自动快照、回滚、`GET /api/config/apply-errors`、SQLCipher 加密存储 | [:link:](https://yarp.aneiang.com/docs/index.html#config-history) |

---

## 服务发现

`ServiceDiscoveryRefreshService` 周期从注册中心解析集群目标端点并发布新的 YARP 快照 —— 全程无需重启。按集群绑定（`service-discovery` 插件绑定）。

| 模式 | 说明 |
|:-----|:-----|
| `Static` | 固定端点列表 |
| `HttpJson` | 返回 JSON 端点列表的通用 HTTP 端点 |
| `Consul` | 健康实例过滤 |
| `Nacos` | `healthyOnly` 过滤 + `hosts` 解析 |
| `Eureka` | JSON + `port.$` 解析 + `UP` 过滤 |
| `Kubernetes` | ServiceAccount token + endpoints subsets 解析 |

配置项：`Mode`（默认 `Static`）、`Endpoint`、`ServiceName`、`Namespace`（`default`）、`Scheme`（`http`）、`RefreshSeconds`（`30`）、`RequestTimeoutSeconds`（`5`）。

---

## 可观测性与生产化（2.3.0.40）

- **Prometheus 指标** —— `GET /metrics` 输出标准文本格式：`gateway_requests_total`、`gateway_request_duration_seconds`（直方图）、`gateway_responses_total{class}`、`gateway_inflight_requests`、`gateway_circuits{state}`、`gateway_config_version`。采集点在代理管道内，不依赖任何插件是否绑定；序列数上限 300，超出并入 `route="other"`，避免基数爆炸。
- **上游连接池** —— `gateway_upstream_connections{host,state}` 用 `MeterListener` 读取 `System.Net.Http` 内置 Meter，区分 active / idle："池打满"和"上游慢"在请求侧长得一样，但从这两条线一眼能分开。
- **TTFR** —— `gateway_serving`（0/1）与 `gateway_first_success_seconds`：从进程启动到第一个成功代理请求。"起来了但一条都没转发"是其他所有指标都看不出的故障态。
- **配置预检** —— `POST /api/config/validate` 在保存前给出带字段定位的错误/提醒；`GET /api/config/integrity` 对全量配置体检（指向不存在集群的路由、无目标节点的集群、重复匹配路径）。**自注册、批量导入、历史回滚、AI 写操作走同一道闸**。
- **策略名不再是盲填** —— 授权 / CORS / 限流 / 输出缓存四个字段改为下拉，内置 9 个 `gw-*` 策略开箱可用；未注册的名字在保存时即被拒绝，而不是等到构建端点时抛异常。
- **乐观并发** —— 写操作携带 `X-Config-Version`，版本不一致返回 `409`，避免两个标签页互相覆盖。
- **单元测试基线** —— `tests/Aneiang.Yarp.UnitTests`，158 个 xUnit 用例覆盖 JSON 容错、配置预检、并发契约、指标渲染、策略目录与连接池采集；`.github/workflows/ci.yml` 每次推送跑编译 + 测试 + 打包检查。

```bash
dotnet test tests/Aneiang.Yarp.UnitTests
curl -s http://localhost:5000/metrics | head
```

Grafana 面板模板随包提供，位于包内 `content/observability/grafana-dashboard.json`（源码仓库路径 `docs/observability/grafana-dashboard.json`）。

---

## 灰度发布（2.3.0.40）

内置 `Weighted` 负载均衡策略，按节点权重**精确**分流，而不是"大致按比例"。

**开启方式**：把集群的 `LoadBalancingPolicy` 设为 `Weighted`，然后在目标节点的 `Metadata` 里写 `Weight`：

```json
{
  "ClusterId": "checkout",
  "LoadBalancingPolicy": "Weighted",
  "Destinations": {
    "v1": { "Address": "http://10.0.0.11:8080", "Metadata": { "Weight": "90" } },
    "v2": { "Address": "http://10.0.0.12:8080", "Metadata": { "Weight": "10", "Tag": "v2" } }
  }
}
```

- **比例是精确的**。实现是平滑加权轮询：每 100 次请求，90/10 就是 90 次与 10 次，且交错分布而不是先连发 90 次。YARP 自带的 `PowerOfTwoChoices` 也读 `Weight`，但只在两个随机节点间用它做平局裁决，实际比例会漂移 —— 灰度决策不能建立在"大概"上。
- **权重 0 = 停车位**。节点保留在配置里但不接流量，适合"先部署、后放量"。
- **按人钉住**：请求头 `X-Gateway-Canary: v2`（填 `Tag`）或 `X-Gateway-Canary: v2`（填节点名如 `d2`）会把这次请求强制打到指定节点。典型顺序是"我先钉住自己验证一遍，再把权重从 0 调到 10"。
- **按目标观测**：`gateway_destination_requests_total{cluster,destination}` 与 `gateway_destination_failures_total{cluster,destination}` 让灰度期间的流量占比和错误率可以逐节点对比；Grafana 模板里对应「Canary split by destination」与「Canary error rate by destination」两块面板。
- **保存前会被检查**：权重不是整数、全部为 0、或者写了 `Tag` 却没启用 `Weighted`，预检都会给出带字段定位的提示。

可覆盖的集群级 metadata：`Weighted.WeightKey`、`Weighted.DefaultWeight`、`Weighted.TagKey`、`Weighted.HeaderName`。

### 自动放量与自动回滚

权重会算了，接下来要有人盯着看 —— 这件事交给灰度状态机，在 Dashboard「集群」页每行的灰度按钮里操作，或者直接调 `POST /api/canary/rollouts`：

```json
{
  "clusterId": "checkout",
  "canaryDestinations": ["v2"],
  "steps": [10, 30, 50, 100],
  "observationWindowSeconds": 120,
  "errorRateThreshold": 0.2,
  "minSamples": 50
}
```

它会自动改权重 → 开观察窗口 → 关窗口时判定 → 推进下一档或退回去。状态只有四种去处：`Completed`（跑完 100%）、`RolledBack`（灰度自己的错）、`Paused`（需要人看一眼）、`Aborted`（人手动停）。

几个刻意这么设计的地方：

- **判定顺序是「样本量 → 绝对阈值 → 归因」**。样本不够只 `Hold` 并延长窗口（最多 3 次），不会在 3 个请求 3 个失败时就回滚 —— 那样的自动回滚等于抖动。阈值达标后又分两种：灰度错误率超阈值、**基线也在超** → `Pause`（`baselineDegraded`），不回滚。回滚既救不了火，还会把现场销毁掉。只有基线健康、唯独灰度坏，才是 `RolledBack`（`canaryDegraded`）。
- **回滚是精确还原，不是写默认值**。开始时会记下 `BaselineExplicitWeights`，回滚时对原本就没有 `Weight` 键的节点**删掉这个键**，而不是写死成 1；集群的 `LoadBalancingPolicy` 也一并还原。
- **权重没生效会被单独诊断**。灰度侧一个请求都没收到时给 `NoCanaryTraffic` 并点名 `Weighted`，不会伪装成"健康"然后一路推到 100%。
- **进程内语义**。指标是进程级的，所以"一个集群三台网关"是三次独立 rollout，各自推进各自回滚。多实例协同留给 ADR-1 的单控制面方案。
- 状态落盘在 SQLite 同目录的 `canary-rollouts.json`；重启后残留的 `Observing` 会自动转成 `Paused`，因为窗口基线已经失效了，不能拿半个窗口继续判。

默认值可在 `appsettings.json` 覆盖：

```json
{
  "Aneiang": {
    "Yarp": {
      "Dashboard": {
        "Canary": {
          "ObservationWindowSeconds": 120,
          "ErrorRateThreshold": 0.2,
          "MinSamples": 50,
          "DefaultSteps": [10, 30, 50, 100],
          "MaxWindowExtensions": 3
        }
      }
    }
  }
}
```

**插件扩展点**：`PluginCapability` 新增 `LoadBalancingPolicy` 与 `Transform`，第三方插件实现 `IGatewayPluginHostServices.ConfigureHostServices(IServiceCollection)` 即可注册自己的策略（注意走的是宿主容器，不是插件的运行时容器 —— 后者注册的策略 YARP 看不见）。

### 指标告警（2.3.0.40）

灰度会自己回滚，但前提是有人在看；没人看的时候，5xx 率、P99、熔断开闸该有人喊一声。位置在 Dashboard「通知管理 → 指标告警」，或直接调 `POST /api/alerts/rules`：

```json
{
  "name": "checkout 5xx",
  "kind": "ErrorRate",
  "scopeKind": "Cluster",
  "scope": "checkout",
  "threshold": 0.05,
  "windowSeconds": 60,
  "forSeconds": 120,
  "minSamples": 50,
  "severity": "Critical",
  "renotifyMinutes": 30,
  "notifyOnResolve": true
}
```

三类指标：`ErrorRate`（窗口内 5xx 占比，`0.05` = 5%）、`LatencyP99`（窗口内 P99 延迟，单位秒）、`CircuitOpen`（范围内的开闸数量）。范围可选 `Global` / `Cluster` / `Route`（熔断器按集群记账，所以它没有路由级）。

几个刻意这么设计的地方：

- **判定顺序是「窗口 → 样本 → 阈值 → 持续」**，少一步都会变成噪声：一个失败请求就是 100% 错误率，所以样本量不足时**根本不判断**；窗口还没填满时也不会拿 15 秒的数据冒充 60 秒的速率；越线但没撑满 `forSeconds` 只是 `Pending`，不通知。
- **无数据不等于恢复**。窗口里没有流量时返回 `InsufficientData`，正在 `Firing` 的告警原地保持 —— 否则"上游全挂了、错误计数也停了"会被读成"错误率 0%，一切正常"，而那正是最不该发这条消息的时刻。
- **窗口是滑动的**。每跳留一份计数器快照，取"至少一个窗口之前的最新快照"做差，因此每一跳都能判断，而不是滚动窗口里一簇流量被平均掉。`ObservedSeconds` 如实上报实际覆盖时长（tick 会越过窗口，面板上写 74s 就不写 60s）。
- **`Critical` 以 `Error` 下发**。推送渠道只认 `Error` / `Warning`，其它字符串会落进"一切正常"的绿色勾。
- **投递状态是可见的**。没有任何平台订阅 `MetricAlert` 事件时，面板顶部直接提示"这些告警只会写进日志"。规则配好了却没人收到，是这类功能最典型的死法。
- **恢复也发一条**，带持续时长与峰值；但只在 `Pending` 阶段就恢复的不发 —— 没人知道它开始过，也就不该有人收到它结束。
- 规则与历史落盘到 SQLite 同目录的 `metric-alerts.json`；重启时残留的 `Firing` 会被关闭并标记 `gatewayRestarted`，因为原来的计数器基线已经不在了，续不上。

相关接口：`GET /api/alerts`（规则 + 告警 + 投递状态）、`POST /api/alerts/rules`、`DELETE /api/alerts/rules/{id}`、`POST /api/alerts/rules/{id}/test`（发一条测试通知，验证渠道确实通）、`POST /api/alerts/incidents/{id}/mute`（静默只关通知，不停测量）。告警引用的数字在 `/metrics` 里都看得见：`gateway_route_failures_total{route,cluster}`、`gateway_request_duration_seconds`、`gateway_circuits{state}`。

默认值可在 `appsettings.json` 覆盖：

```json
{
  "Aneiang": {
    "Yarp": {
      "Dashboard": {
        "Alerting": {
          "TickSeconds": 15,
          "MaxIncidents": 200,
          "DefaultWindowSeconds": 60,
          "DefaultForSeconds": 60,
          "DefaultMinSamples": 20,
          "DefaultErrorRateThreshold": 0.05,
          "DefaultLatencyP99Seconds": 1.0
        }
      }
    }
  }
}
```


---

## 链路追踪（2.3.0.40）

指标能告诉你"错了多少"，回答不了"错在哪一跳"。开了追踪之后，一次请求在网关里的每一段都有自己的 span，位置在 Dashboard「链路追踪」页（或直接 `GET /api/tracing`）：

```
GET /api/baidu/{**catchAll}          0.0ms  ───────────────────────────────  26.1ms   Microsoft.AspNetCore
  proxy.forwarder                    0.3ms  ──────────────────────────────   25.8ms   Yarp.ReverseProxy
    gateway.forward                  0.3ms  ─────────────────────────────   25.7ms   route=baidu_api_route
      gateway.plugin rate-limit      0.3ms  ──────────────────────────────  25.4ms
        gateway.plugin circuit-breaker / request-retry / response-cache / proxy-log / traffic-metrics
          GET (System.Net.Http)      0.8ms  ────────────────────────────    24.2ms
```

`gateway.forward` 上带着 `gateway.route.id` / `gateway.cluster.id` / `gateway.destination.id` / `http.response.status_code` / `gateway.short_circuited`，所以"哪个节点慢"和"是不是被插件拦了"都能直接读出来，不用再翻日志。

几个刻意这么设计的地方：

- **埋点在核心库，导出在 Dashboard**。`Aneiang.Yarp` 只 `new ActivitySource("Aneiang.Yarp")`，用的是框架自带的 `System.Diagnostics.DiagnosticSource`，**不加任何依赖**；OTel SDK、instrumentation、OTLP exporter 全在 `Aneiang.Yarp.Dashboard`。不开追踪的网关不该被动背上 exporter，更不该在半夜刷"连不上 collector"。
- **第三方插件零改动就被追踪**。宿主用 `TracedProxyPipelineBuilder` 包住 `IReverseProxyApplicationBuilder`，插件拿到的 `Use()` 自动套一层以插件 id 命名的 span，`ConfigureProxyPipeline` 的签名没变。
- **只有 5xx 和转发错误才标 `Error`**，4xx 不标（客户端错误不是网关失败，这是 OTel 的约定）。`gateway.short_circuited=true` 表示请求被插件拦下 —— 既没有目标节点也没有转发错误，与"上游挂了"分开。
- **采样是确定性的**：按 traceId 哈希判定，同一条 trace 永远同一结论，不会记了父 span 丢了子 span；**入站 `traceparent` 的 sampled 标记优先于本地比例** —— 覆盖它会把链路正好在网关处劈成两半。采样率可在面板上用滑杆改，下一个请求即生效。
- **控制面请求不追踪**。`/{RoutePrefix}`、`/_content`、`/metrics`、`/health|/ready|/live`、`/favicon.ico` 全部排除 —— 追踪页 5 秒轮询否则会把自己填满，把真实流量挤出去。
- **不装 collector 也能看**。`ExportEnabled: false` 时 span 只进内存缓冲，瀑布图照常渲染。缓冲区三重上限（trace 数 / 每 trace span 数 / 每 span tag 数与长度），且**绝不持有 `Activity` 引用**（那会 pin 住 `HttpContext` 和请求体），span 一结束就拍平成字符串。
- **启动失败不阻断进程**。endpoint 写错或 collector 不可达时只记下错误，网关照常转发，原因显示在追踪页顶部 —— 网关不该因为观测系统不可用而起不来。
- **数字的含义要说清**：页面上的「采样判定 / 丢弃判定」是**判定次数**，含被过滤的控制面请求（采样发生在过滤之前），会大于实际捕获的链路数；"我有多少条链路"看「内存缓冲」。

导出到 Jaeger（一行拉起一个），`Endpoint` 指到 `4317`（gRPC）或 `4318`（http/protobuf）：

```bash
docker run -d --name jaeger -p 16686:16686 -p 4317:4317 jaegertracing/all-in-one:latest
```

```json
{
  "Gateway": {
    "Dashboard": {
      "Tracing": {
        "Enabled": true,
        "ExportEnabled": true,
        "Endpoint": "http://localhost:4317",
        "Protocol": "grpc",
        "ServiceName": "aneiang-gateway",
        "Environment": "production",
        "SamplingRatio": 0.1,
        "RecordPluginSpans": true,
        "RecentTraceCapacity": 100,
        "MaxSpansPerTrace": 64
      }
    }
  }
}
```

> 追踪配置挂在 `Gateway:Dashboard` 之下，和 `RoutePrefix` / `Auth` 同层。默认 `Enabled: false`：缺省不产生任何 span，也不注册 exporter。`Enabled` 改动需要重启（provider 启动时构建一次），采样率不需要。

相关接口：`GET /api/tracing`（状态 + 最近 N 条 trace）、`GET /api/tracing/traces/{traceId}`（完整 span 列表）、`POST /api/tracing/sampling`（`{"ratio":0.1}`）、`POST /api/tracing/clear`。

---

## 故障注入（2.3.0.40）

前面几块能力（重试、熔断、指标、告警、自动回滚）都是"等真故障发生时才验证"的东西 —— 而故障总是发生在最不方便的时候，也总在你没盯着面板的时候。故障注入把这件事反过来：**主动把故障造出来，然后看那些组件有没有按你说的反应。**

配置在「插件中心 → 稳定性 → 故障注入」，绑定到具体路由，走 JSON Schema 校验：

```json
{
  "enabled": true,
  "probability": 1.0,
  "delayMinMs": 400,
  "delayMaxMs": 800,
  "errorStatusCode": 503,
  "errorBody": "{\"error\":\"upstream_unavailable\"}",
  "abortConnection": false,
  "requiredHeader": "X-Chaos",
  "requiredHeaderValue": "on",
  "announce": true
}
```

三类故障可以单独用，也可以叠在一起：**延迟**（在 `delayMinMs`–`delayMaxMs` 之间随机）、**错误状态码**（400–599 + 自定义响应体）、**连接中断**（模拟传输层故障，而不是让上游回一个 5xx —— 这两种情况熔断和重试的处理并不一样，都该演练）。

几个和"敢在真实环境用"直接相关的设计：

- **位置是刻意的：它排在代理管线最内侧，紧挨 forwarder。** 只有在这个位置，注入的 5xx 对重试、熔断、指标、告警、灰度回滚来说才**与上游真的坏了无法区分** —— 重试会重试它、熔断会记它、`gateway_route_failures_total` 会加一、告警会响、灰度会因此退回去。放到最外层，这些组件根本看不见故障，演练会"全部通过"，而**一份假的信心比不做演练更危险**。
- **默认什么都不做。** 除了 `enabled`，每个字段都关着；只勾了"启用"的绑定不会改变任何请求。演练必须自己写清要注入什么，否则"跑了一遍没报错"很容易被当成"系统很稳"。
- **`requiredHeader` 是"敢在生产演练"的前提。** 配上之后只有带该头的请求才被注入，真实用户完全不受影响，你可以拿一个 `curl` 反复验证同一条路由。
- **`probability` 支持"10% 的请求失败"。** 想证明告警会响不需要把整条路由打死。
- **`abortConnection` 与 `errorStatusCode` 同时配置时以中断为准** —— 自相矛盾的配置不做猜测。
- **`announce`（默认开）会给响应加 `X-Fault-Injected: 503`。** 演练最怕的是事后分不清"这次是注入的还是真出了事"，这个头就是那条分界线。要绝对真实的链路（比如验证客户端在裸 5xx 下的表现）可以关掉。
- **延迟遵守 `RequestAborted`**，客户端放弃就停止计时，否则长延迟会攒下一堆只能等超时的任务；上限 2 分钟，防止手滑把整条路由挂住。
- **日志只在每个路由首次注入时打 `Warning`**，之后降到 `Debug` —— 100% 比例的演练不该让解释演练的日志把自己淹掉。

**"一键关闭"指的是关掉这条绑定，不是关掉这个插件。** 插件中心会拒绝禁用"还有绑定的插件"（`409 Remove plugin bindings first: route:xxx`），这是为了防止误点把整场演练的配置一起丢掉。演练结束时把绑定的 `enabled` 关掉：下一个请求立刻不再注入，配置原样保留，随时可以重开同一场演练。

```bash
# 立刻停止注入（配置保留）；{prefix} 是 Dashboard 的 RoutePrefix，默认 apigateway
curl -X POST http://localhost:5000/apigateway/api/plugin-bindings/batch-enabled \
  -H "Content-Type: application/json" \
  -d '{"bindingIds":["<binding-id>"],"enabled":false}'
```

也可以在面板上一次把这条绑定禁用，效果相同。

> 一个容易踩的点：**注入发生在重试内侧**，所以给配了重试的路由注入 503，实际会按重试策略多打几次，日志里也会看到多次注入记录。这是对的 —— 重试本来就该看见它。反过来说，演练"重试能不能救回来"时这正是你要的现象；演练"告警会不会响"时记得把持续时间算进去。

---

## 多环境（2.3.0.40）

一份配置管一个环境。想把 dev / staging / prod 分开，就得有**两个不同的问题**各自的答案：网关**正在转发**哪个环境的配置，以及你**正在编辑**哪个环境。这两件事在大多数工具里被合成一个开关，于是"打开 staging 看一眼"就等于把生产流量切到 staging。

这里的做法是彻底分开：

| | 是什么 | 能不能改 |
|:--|:--|:--|
| **运行环境**（runtime） | 网关实际转发用的那套配置 | ❌ 进程启动时固定，运行期不可改 |
| **编辑视角**（editing） | 面板所有页面读写的那套配置 | ✅ 随时切换，不影响转发 |

打开面板时两者相同。切到别的环境后，页面上的路由、集群、插件绑定全部变成该环境的数据，而**转发一点没动**；此时保存任何配置只会写进那个环境的存储，快照发布被拦下。

```json
{
  "Gateway": {
    "Storage": {
      "Sqlite": { "ConnectionString": "Data Source=gateway-store.db" },
      "Environments": {
        "Runtime": "prod",
        "Names": [ "prod", "staging", "dev" ]
      }
    }
  }
}
```

**实现是"每环境一个库文件"，不是每张表加一列。** 隔离是文件系统级的，dev 库怎么折腾都碰不到 prod：

| 环境 | 库文件 |
|:--|:--|
| `prod`（运行环境） | `gateway-store.db` — 与配置里写的**完全相同** |
| `staging` | `gateway-store.staging.db` |
| `dev` | `gateway-store.dev.db` |

两点值得单独说：

- **运行环境沿用原连接串**，所以已有部署升级时配置、历史、审计日志全在原处，**不需要任何迁移**。如果按环境名一律加后缀，升级完会发现配置"全没了"。
- **不改任何表结构、任何 SQL、任何仓储**。这也是为什么这个特性没有把既有 358 个测试改一遍。

切换在面板顶部：编辑运行环境时控件是灰色 + 绿色 `LIVE` 徽标；编辑别的环境时变琥珀色，并直接写明"仍在转发 prod"。单环境部署时整个控件不显示，行为和升级前一致。

```bash
# 读：运行环境、当前编辑环境、可用列表
curl http://localhost:5000/apigateway/api/environments

# 写：切编辑视角（不影响转发）
curl -X POST http://localhost:5000/apigateway/api/environments/switch \
  -H "Content-Type: application/json" -d '{"environment":"dev"}'
```

几个使用上的事实：

- **切换会重载工作集并刷新页面**，未保存的表单内容会丢 —— 确认框里写了这一点。
- **日志、指标、链路追踪是运行态数据**，切到没跑过流量的环境后这些页面会是空的。这不是 bug：那个环境确实没有流量。
- **环境名只允许字母、数字、`_`、`-`**，因为它要进文件名。`../prod` 这类名字在配置加载时就会报错，而不是等到打开库的时候。
- **提升（把 dev 的配置推到 prod）还没做**，属于 2.3.0.40 之后的批次（环境间 diff → 一键提升 → 审批）。

---

## 我们明确不做的事

写清楚边界，是为了避免选型时误判 —— 下面这些不是"还没做"，而是**有意不做**：

| 不做 | 原因 | 该用什么 |
|:--|:--|:--|
| **API 门户 / 开发者自助控制台** | 会把这套东西变成第二个 Kong Dev Portal，但网关的核心价值在数据面稳定，不在文档托管 | 单独的门户产品，或用 OpenAPI 生成静态站 |
| **自带 IdP / 用户目录** | 认证是专业领域，自建账号体系只会让安全边界变得更差 | Keycloak、Authing、Azure AD；网关只做 token 校验与转发 |
| **日志存储与检索引擎** | 网关侧只做采集与转发，存与查是 Elasticsearch / Loki 的活 | `proxy-log` 插件输出 + 外部日志平台 |
| **Service Mesh 控制面** | sidecar 模型与网关的"入口集中治理"是两种架构，混在一起会把两边的复杂度叠加 | Istio / Linkerd |
| **Kubernetes Ingress Controller** | 需要 CRD、调谐循环与 K8s 生命周期语义，是另一个产品而不是一个特性 | ingress-nginx、Traefik、YARP 官方 Ingress 方案 |
| **分布式限流（跨实例共享计数）** | 依赖外部计数器（Redis）会引入新的故障域，单机限流已覆盖绝大多数入口场景 | 在 `rate-limit` 之前挂 Redis 限流中间件，或按实例数做配额除法 |

---

## 内置插件

| ID | 作用域 | 功能 |
|:---|:---|:-----|
| `waf` | Route | IP 黑白名单、SQLi / XSS / 路径遍历检测、请求限制、安全响应头 |
| `rate-limit` | Route | 固定窗口 / 滑动窗口 / 令牌桶 / 并发 |
| `request-retry` | Route | 指数退避 + 抖动重试，状态码 / 异常策略 |
| `circuit-breaker` | Cluster | 失败阈值、恢复超时、半开探测 |
| `compression` | Route | 按 MIME 白名单 + 最小尺寸的 Gzip / Brotli 压缩 |
| `response-cache` | Route | 有界内存缓存，TTL 与 Vary 键 |
| `traffic-metrics` | Route | 按路由的请求 / 错误 / 延迟 / 字节 |
| `cluster-metrics` | Cluster | 按集群的请求 / 错误 / 延迟 / 目标节点 |
| `proxy-log` | Route | 全量代理捕获（头 / 可选 Body）、采样 |
| `service-discovery` | Cluster | 周期注册中心端点刷新 |
| `fault-injection` | Route | 延迟 / 错误码 / 连接中断注入，用于演练重试、熔断、告警与回滚 |

---

## 文档中心

完整文档位于 **[yarp.aneiang.com](https://yarp.aneiang.com)**。

- **快速入门** — [项目概览](https://yarp.aneiang.com/docs/index.html#overview) · [快速开始](https://yarp.aneiang.com/docs/index.html#quickstart)
- **核心网关** — [认证鉴权](https://yarp.aneiang.com/docs/index.html#authentication) · [动态路由](https://yarp.aneiang.com/docs/index.html#dynamic-routing) · [IP 隔离](https://yarp.aneiang.com/docs/index.html#ip-isolation) · [配置参考](https://yarp.aneiang.com/docs/index.html#configuration)
- **Dashboard** — [Dashboard 总览](https://yarp.aneiang.com/docs/index.html#dashboard-overview) · [配置历史](https://yarp.aneiang.com/docs/index.html#config-history)
- **安全防护** — [WAF 防火墙](https://yarp.aneiang.com/docs/index.html#waf-firewall) · [策略管理](https://yarp.aneiang.com/docs/index.html#policy-management)
- **监控告警** — [健康检查](https://yarp.aneiang.com/docs/index.html#health-check) · [熔断器](https://yarp.aneiang.com/docs/index.html#circuit-breaker) · [限流](https://yarp.aneiang.com/docs/index.html#rate-limiting) · [请求日志](https://yarp.aneiang.com/docs/index.html#request-logs) · [请求重试](https://yarp.aneiang.com/docs/index.html#request-retry) · [通知](https://yarp.aneiang.com/docs/index.html#notifications)
- **插件与管道** — [插件系统](https://yarp.aneiang.com/docs/index.html#plugin-system) · [中间件管道](https://yarp.aneiang.com/docs/index.html#middleware-pipeline)
- **客户端 SDK** — [客户端自动注册](https://yarp.aneiang.com/docs/index.html#client-registration)
- **AI 助手** — [AI 智能助手](https://yarp.aneiang.com/docs/index.html#ai-assistant)
- **高级用法** — [部署模式](https://yarp.aneiang.com/docs/index.html#deployment-modes) · [自定义认证](https://yarp.aneiang.com/docs/index.html#custom-auth) · [生产环境配置](https://yarp.aneiang.com/docs/index.html#production-config)
- **存储层** — [SQLite 存储](https://yarp.aneiang.com/docs/index.html#storage-sqlite)

---

## 贡献规范

欢迎任何人参与贡献！

1. Fork 本仓库并创建功能分支（`git checkout -b feature/your-feature`）。
2. 提交时使用清晰的提交信息，说明改动内容与目的。
3. 保持 **API 接口稳定** —— 新功能尽量以插件形式提供。
4. 新增插件请遵循 `IGatewayPlugin` 清单约定（ID、显示名、版本、作用域、能力），并提供 JSON Schema。
5. 修改 Dashboard 文案时，同步更新 `zh-CN` 与 `en-US` 两套语言包。
6. 提交 Pull Request 前请完整编译，确保 **0 警告 / 0 错误**。

请通过 GitHub Issues 反馈 Bug 与功能建议。所有第一方包均采用 MIT 许可。

---

## NuGet

| 包 | NuGet |
|:--------|:-----:|
| **Aneiang.Yarp** | [![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.svg)](https://www.nuget.org/packages/Aneiang.Yarp) |
| **Aneiang.Yarp.Dashboard** | [![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.Dashboard.svg)](https://www.nuget.org/packages/Aneiang.Yarp.Dashboard) |
| **Aneiang.Yarp.Client** | [![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.Client.svg)](https://www.nuget.org/packages/Aneiang.Yarp.Client) |
| **Aneiang.Yarp.Storage.Abstractions** | [![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.Storage.Abstractions.svg)](https://www.nuget.org/packages/Aneiang.Yarp.Storage.Abstractions) |
| **Aneiang.Yarp.Storage.Sqlite** | [![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.Storage.Sqlite.svg)](https://www.nuget.org/packages/Aneiang.Yarp.Storage.Sqlite) |
| **Aneiang.Yarp.Grpc** | [![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.Grpc.svg)](https://www.nuget.org/packages/Aneiang.Yarp.Grpc) |
| **Aneiang.Yarp.Plugin.Abstractions** | [![NuGet](https://img.shields.io/nuget/v/Aneiang.Yarp.Plugin.Abstractions.svg)](https://www.nuget.org/packages/Aneiang.Yarp.Plugin.Abstractions) |

**.NET 8.0 / 9.0** · **YARP 2.3.0**

---

## 许可证

[MIT](LICENSE)

---

<div align="center" style="display: flex; justify-content: center; align-items: center; gap: 20px;
            background: #f6f8fa; padding: 20px 30px; border-radius: 12px;
            border: 1px solid #e1e4e8; box-shadow: 0 2px 8px rgba(0,0,0,0.05);">
  <img src="docs/wechat_qrcode.jpg" alt="公众号二维码"
       style="width: 140px; height: 140px; border-radius: 8px; border: 2px solid #d0d7de;" />
  <div style="text-align: left;">
    <h3 style="margin: 0 0 4px 0; font-size: 22px; font-weight: 600; color: #24292e;">
      递归不爆炸
    </h3>
    <p style="margin: 0; font-size: 16px; color: #586069; max-width: 280px;">
      扫码关注，获取更多精彩内容
    </p>
  </div>
</div>

<div align="center">

觉得有用？[⭐ Star 一下](https://github.com/aneiang/Aneiang.Yarp) 支持项目发展

</div>