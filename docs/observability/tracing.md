# 链路追踪导出配置

网关把 span 通过 OTLP 导出到 Jaeger / Tempo / SigNoz / Grafana Cloud 等后端。本文只讲**怎么配**和**配错了怎么查**，追踪的整体设计见路线图 §6。

配置段：`Gateway:Dashboard:Tracing`。默认 `Enabled: false`——导出这件事决定了每个请求的数据去哪儿，不该有默认值。

## 最小可用配置

```json
{
  "Gateway": {
    "Dashboard": {
      "Tracing": {
        "Enabled": true,
        "ExportEnabled": true,
        "ServiceName": "my-gateway",
        "Endpoint": "http://tempo:4318",
        "Protocol": "http/protobuf"
      }
    }
  }
}
```

`ExportEnabled: false` 时不开导出器，span 只进面板的环形缓冲——用于"没有 collector 也想看一眼链路"的场景。

## 鉴权

后端要求鉴权时，用 `Headers`。它是一个字典，会附加到**每一次**导出请求上。

```json
"Headers": {
  "Authorization": "Basic <base64(user:password)>",
  "X-Scope-OrgID": "tenant-a"
}
```

### 三个来源，按优先级合并

| 优先级 | 来源 | 写法 | 典型用途 |
|:--|:--|:--|:--|
| 1（最高） | 配置文件 | `Gateway:Dashboard:Tracing:Headers:{Name}` | 部署时的固定决策，和其他配置放一起便于评审 |
| 2 | 面板 | 追踪页「管理」→ 写入 `Data/tracing-auth.json` | 临时更换 token，不必重新发版 |
| 3 | 标准环境变量 | `OTEL_EXPORTER_OTLP_HEADERS` / `OTEL_EXPORTER_OTLP_TRACES_HEADERS` | 容器/K8s 已经把 secret 放在这里了 |

**按 header 名逐项合并，不是整份覆盖**：在面板里改 `Authorization` 不会丢掉配置文件里的 `X-Scope-OrgID`。

环境变量的格式是 `key1=value1,key2=value2`，按第一个 `=` 切分，所以 base64 的 `==` padding 会完整保留：

```bash
OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic YWJjZA=="
```

### 环境变量写法（.NET 配置系统的嵌套键）

```bash
Gateway__Dashboard__Tracing__Headers__Authorization="Basic <base64>"
Gateway__Dashboard__Tracing__Headers__X-Scope-OrgID="tenant-a"
```

### 面板写入

追踪页「数据出口」列会显示 `已鉴权 N 项` 与脱敏值（如 `Basic ****cFFxb1Y=`），并在旁边标明来源。点「管理」可以修改：

- **值永远不回显**，接口只返回脱敏形式；
- **来自配置文件的 header 在面板里是锁定的**（灰显、不能删）。它优先级最高，面板写什么都压不住，所以干脆不允许在这里改——避免"保存成功但没生效"；
- 某一行的值**留空表示不使用面板覆盖**，会回退到配置文件或环境变量——不是"把凭据清空"；
- 保存后**必须重启网关**。导出器在启动时构建一次，改完不重启时页面会显示新值、但实际发出的还是旧值，这一点在弹窗里明确提示；
- 只有 `admin` 能改：导出目标决定数据流向谁，与 webhook 目标同级；
- 写入的是 `Data/tracing-auth.json`，Linux 下权限 `0600`，不改写主 `appsettings.json`（避免重排格式、丢掉注释）。

清空所有行即删除覆盖文件，完全回退到配置与环境变量。

## 协议与端点

`Protocol` 只接受两个值，大小写与 `-` `_` `/` 差异会被归一化：

| Protocol | 含义 | 常见端口 |
|:--|:--|:--|
| `grpc` | OTLP over gRPC | 通常 4317（各家可能不同，以部署实际监听为准） |
| `http/protobuf` | OTLP over HTTP + protobuf | 标准 4318；OpenObserve 5080 |

**写错不会被静默纠正。** 早期版本只认精确匹配的 `http/protobuf`，其他任何值（包括 `httpprotobuf`、`http-protobuf`）都会**静默退化成 gRPC**，症状是"collector 连不上"，把人引向网络排查。现在归一化后仍不认识的值会让导出器启动失败，并在追踪页显示原因。

### `http/protobuf` 的 `/v1/traces` 由网关补齐

导出器 1.15.3 **不会**追加 `/v1/traces`。这一点是本机抓包实测的：Endpoint 写成什么路径，请求就打到什么路径。而绝大多数 OTLP 教程还停留在"SDK 会自动补尾段"的年代，照抄的结果是少一段路径、后端返回 404，而 404 又很容易被误读成"token 不对"。

所以网关在启动时统一补齐一次：

| 配置的 Endpoint | 实际请求 |
|:--|:--|
| `http://collector:4318` | `http://collector:4318/v1/traces` |
| `http://collector:4318/v1/traces` | 原样，不重复 |
| `http://obs:5080/api/default` | `http://obs:5080/api/default/v1/traces` |

启动日志会打印最终地址，排查时先看这一行：

```text
OTLP export URL: http://openobserve:5080/api/default/v1/traces
```

gRPC 模式**不做**这个处理：gRPC 的方法路径由协议自己拼，Endpoint 里带路径反而会把方法名拼坏，所以那里只写 `scheme://host:port`。

## 凭据放在哪里

`Headers` 里是**第三方后端的凭据**，不是本网关的。它等于把"谁能写数据到你后端"的钥匙放在了网关这边，所以位置比格式更重要。

**不要写进入库的 `appsettings.json`。** 这类文件会进 Git，一次提交之后凭据就永久留在历史里，删掉当前行也没用。三个正确位置，按场景选：

| 位置 | 适合 | 注意 |
|:--|:--|:--|
| 环境变量 / K8s Secret | 生产。凭据不进镜像、不进仓库 | `OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic xxx,stream-name=default"` |
| `appsettings.Development.json` | 本地开发。仓库里默认忽略 | 见本仓库 `.gitignore`；`appsettings.json` 只留占位符 |
| 面板「导出鉴权」 | 临时换 token，不想重启后还留着旧值 | 写到 `Data/tracing-auth.json`，Linux `0600`；仍需重启才生效 |

示例项目就是这样分的：`samples/SampleGateway/appsettings.json` 里只有 `Basic <base64(...)>` 占位符，真实值在 `appsettings.Development.json`（已忽略）。占位符解析不成真实值时会原样发出去，得到一个 401 —— 这种报错容易被当成"密码错了"，所以代码在检测到值里还有 `${` 时会记一条 warning。

## 各后端的实际填法

### OpenObserve（5080，`/api/{org}`）

OpenObserve 的 OTLP 入口在设计上是 HTTP 与 gRPC **共用 5080**，组织名走路径（HTTP）或 header（gRPC）。但实测多台实例的 5080 **只服务 HTTP/1.1，不接受明文 HTTP/2**，所以 gRPC 那条实际走不通——**用 OTLP HTTP**，不要混着配。

**OTLP HTTP（推荐，路径里带 org，错误更容易看出来）**

```json
"Tracing": {
  "Enabled": true,
  "ExportEnabled": true,
  "Endpoint": "http://openobserve:5080/api/default",
  "Protocol": "http/protobuf",
  "Headers": {
    "Authorization": "Basic <base64(user:password)>",
    "stream-name": "default"
  }
}
```

- `Endpoint` 写到 `/api/default` 为止，`/v1/traces` 由网关补（写全也不会重复）；
- `organization` **不用**写，路径里的 `api/default` 就是它；
- `stream-name` 可省略，省略即写入默认 stream。

**OTLP gRPC（在这类实例上不可用，仅作对照）**

```json
"Tracing": {
  "Enabled": true,
  "ExportEnabled": true,
  "Endpoint": "http://openobserve:5080",
  "Protocol": "grpc",
  "Headers": {
    "Authorization": "Basic <base64(user:password)>",
    "organization": "default",
    "stream-name": "default"
  }
}
```

- gRPC 的路径不携带 org，**`organization` 必须写成 header**，漏了会被后端按默认组织处理或直接拒；
- 明文传输（后端配置里的 `tls.insecure: true`）由 `http://` 这个 scheme 表达，**没有单独的 insecure 开关**；
- **端口要写全**。后端给的 endpoint 若省略端口，OTel 会默认按 4317 处理；如果后端实际不在 4317 上，表现就是"连不上"。这种配置片段通常是给 OTel Collector 用的，端口语义未必和直连一致，以实际监听为准。
- 就算 h2c 通了也未必能进：同一个 5080 用 HTTP/1.1 打 gRPC 方法路径，回的是 `401` + **`WWW-Authenticate: Bearer realm="openobserve"`**，也就是说这个路由要 Bearer（ingest token），不收 Basic。用同一把 Basic 凭据打 `/api/default/v1/traces` 却是 200——**401 不代表凭据错，可能是这个入口不接受这种凭据类型**。

### 其它后端

| 后端 | Protocol | Endpoint |
|:--|:--|:--|
| Jaeger（自带 collector） | `http/protobuf` | `http://jaeger:4318` |
| Grafana Tempo | `http/protobuf` | `http://tempo:4318` |
| SigNoz | `grpc` | `http://signoz-otel-collector:4317` |
| Grafana Cloud | `http/protobuf` | zone 对应的 `otlp-gateway` 地址 + `Authorization: Basic <instanceId:token>` |

## 导出连通性自检

追踪页「数据出口」列有个「测试」按钮（仅 admin）。它向**当前配置的** Endpoint 发一个空 OTLP 批次，把结果直接显示在地址下面。

**为什么需要它**：页面的链路列表由 tracer provider 的 processor 供给，和导出器是同一个 provider 上的两条独立分支。Endpoint 写错时导出器每批都失败，但失败是异步的、重试几次后静默丢弃，错误走 EventSource 而不进应用日志——于是页面看起来一切正常，数据一条也到不了后端。这个按钮是唯一能当场证伪的入口。

它接受两种请求形态：

| 协议 | 实际发出的请求 |
|:--|:--|
| `http/protobuf` | `POST {Endpoint 补齐 /v1/traces}`，body 为空的 `ExportTraceServiceRequest`，`Content-Type: application/x-protobuf` |
| `grpc` | `POST {scheme://host:port}/opentelemetry.proto.collector.trace.v1.TraceService/Export`，5 字节空帧，强制 HTTP/2（h2c） |

### 结果分型

| phase | 含义 | 通常的动作 |
|:--|:--|:--|
| `config` | 配置本身不可用：`ExportEnabled=false`、协议写错、Endpoint 不是绝对 http(s) URL | 改配置 |
| `dns` | 主机名解析不了 | 查拼写、查容器 DNS |
| `connect` | 端口拒绝或不可达 | 查端口号、防火墙、服务是否在跑 |
| `tls` | TLS 握手失败 | `https://` 与后端是否启用 TLS 是否一致 |
| `protocol` | 端口通了但不接受 HTTP/2 明文（gRPC 模式） | 见下节「gRPC 与端口」 |
| `timeout` | 期限内没有任何响应 | 地址不通，或本机 DNS 极慢（见下） |
| `http` | 后端回了非 2xx，或 gRPC trailer 里 `grpc-status != 0` | 看同一个结果里的状态码与响应片段 |

401 与 404 会被明确区分：前者是凭据不被接受，后者几乎总是路径少一段（HTTP 模式漏了 `/api/<org>`，或 gRPC 模式把路径塞进了 Endpoint）。这两件事过去都表现为"连不上"，最容易把人引向轮换 token。

401 还会进一步区分两种情形：后端如果回了 `WWW-Authenticate`，结果里会把这条头原样显示出来，并把 note 标成 `auth-challenge`。此时**凭据本身可能是对的**，是"这个入口不接受这种凭据类型"——例如 OpenObserve 的 gRPC 路由要 `Bearer`，而配置里给的是 `Basic`。这时候该换协议或换凭据类型，不是去改密码。

### 它能证明什么

- 这个 URL 能打到后端的 ingest 处理器；
- 这组鉴权头被接受。

**不能**证明 span 已入库——空批次没有任何数据可索引。要确认端到端，只有去后端按 `service_name` 查。

### gRPC 与端口

gRPC 模式下会先做一次 TCP 连通性检查，再做 h2c 请求。原因：`RequestVersionExact` 会把**所有**失败都报成同一个 `VersionNegotiationError`，端口关闭、域名不存在也一样。没有这一步，"主机名写错"会被显示成"协议不匹配"。

实测过一台只开 5080 的 OpenObserve：5080 能连但不接受 h2c，5081 没有监听。结果分别是 `protocol` + `grpc-no-h2c` 和 `timeout`——也就是说 gRPC 在这台机器上根本不可用，OTLP/HTTP 才是能走的路。**不要把 HTTP 端口的地址配给 gRPC**，很多后端（含 OpenObserve）的 gRPC 在另一个端口。

### 超时与慢 DNS

探测的期限是 5 秒。某些环境里解析一个不存在的名字本身就要 6–8 秒，于是结果落在 `timeout` 而不是 `dns`。这不矛盾：它表达的是"5 秒内没有响应"。想区分就单独跑一次 `nslookup <host>`——如果本机解析也很慢，先修 DNS。

## 排查清单

| 现象 | 先看什么 |
|:--|:--|
| 追踪页显示"启动失败" | 页面会直接显示 `StartError`。协议写错、Endpoint 不是绝对 http(s) URL 都在这里报 |
| 页面上有数据，后端却没有 | 这是两条链路：列表来自 processor，导出走 exporter。点「数据出口」列的「测试」按钮，比对着日志猜快得多 |
| 面板有 span，后端没有 | 导出失败对业务是静默的。先点「测试」；再确认 `ExportEnabled` 为 `true`、采样率不为 0 |
| 后端 401/403 | 看页面「已鉴权」是否为 0 项；确认 header 名拼写、以及 base64 是否包含 `user:password` 的冒号 |
| 后端 404 | HTTP 模式少写了组织路径（OpenObserve 要 `/api/default`）；或 gRPC 模式把路径塞进了 Endpoint |
| gRPC 报 TLS/handshake 错 | Endpoint 写成了 `https://` 但后端是明文。用 `http://` 让 SDK 走 h2c |
| 配了环境变量却没效果 | 确认变量名是 `OTEL_EXPORTER_OTLP_HEADERS`（或 `..._TRACES_HEADERS`），且容器里确实注入了；配置文件里的同名项会覆盖它 |
| 改完面板不生效 | 需要重启。导出器在启动时构建，运行期不支持热换凭据 |
| 页面显示"等重启生效"但重启后还是旧值 | 该 header 在 `appsettings.json` 里也有，配置优先级最高。面板里这一行会显示为锁定状态，改配置文件才有效 |

## 安全约定

- 凭据**只出现在**配置文件、环境变量或 `Data/tracing-auth.json`，不进数据库、不进面板响应；
- 接口返回的一律是脱敏值（保留 scheme 与末 8 位，便于区分两把 token）；
- 值里含换行会被拒绝——否则可以借此向每次导出注入任意 header；
- 提交脱敏值本身也会被拒绝，避免把可用 token 替换成字面量 `****`；
- 面板写入的文件在 Linux 下为 `0600`；
- 探测接口**不接受调用方传 URL**，目标永远是配置里的 Endpoint——它不能被指向运维人员本就到不了的地方，也不会变成开放中继；返回的 URL 会去掉 userinfo；
- 探测接口为 admin-only：它会使用导出凭据，并让网关代调用方发出一次请求。

> 提示：鉴权头是凭据，截图、工单、日志里贴出来等同于泄露。Base64 只是编码，不是加密。
