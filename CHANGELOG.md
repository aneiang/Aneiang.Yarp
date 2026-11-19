# 更新日志

## [2.3.0.40] - 2026-11-18

> **统一发布版本**：`2.3.0` 是 YARP 的版本，`40` 是 Aneiang.Yarp 的版本，合起来就是 `2.3.0.40`。2.3.0.30 之后的所有改动都归入本版，不再按里程碑分开发布 —— 原 2.4.0、2.4.1、2.5.0、2.6.0 四批一次交付。下文出现的 2.4.x / 2.5.x / 2.6.x 指开发过程中的里程碑批次，对外版本一律是 `2.3.0.40`；历史小节（2.3.0.30 及更早）保持原版本号不变。

### 🛡 安全整改（19 项全落地）

按 `docs/security/security-remediation.md` 的四批计划执行完毕。清单里 19 项从"1 项已完成、17 项零改动"变为全绿，`dotnet build` 0 警告 0 错误，单元测试 668 → 764，全解决方案 `dotnet list package --vulnerable --include-transitive` 无易受攻击包。

其中四项值得单独说，因为**实际做法和清单原方案不一样**：

- **依赖漏洞不能靠 pin 压住**（SEC-14）。清单原方案是"显式引用 `bundle_e_sqlcipher` 2.1.11+ 压住传递版本"，实测压不住 —— `Microsoft.Data.Sqlite` 仍会引入 `bundle_e_sqlite3`，NuGet 把 `lib.e_sqlite3` 解析到 2.1.10，CVE 还在。实际做法是改用 `Microsoft.Data.Sqlite.Core` + 显式 `bundle_e_sqlcipher` 2.1.11，从依赖图里移除 `bundle_e_sqlite3`（`SqliteNativeProvider` 补上 Core 版需要的手工 provider 初始化），漏洞是消失而不是被遮住
- **数据库加密：实测确认生效，并加了启动自检**（SEC-15）。两条独立证据：`PRAGMA cipher_version` 有返回值，且带 `Password=` 写库后对 `.db` / `-wal` 做 UTF-8 与 UTF-16 全字节扫描搜不到哨兵串，错密钥打开报 `SQLITE_NOTADB`(26)。既然配置承诺了加密，就不再允许"配了密码但不生效"：连接串带 `Password=` 而 provider 不能加密时，`SqliteConnectionFactory` 构造即抛异常
  - ⚠️ **破坏性变更**：旧版本里若是 `bundle_e_sqlite3` 抢到了 provider 初始化，写出的库是明文；升级后这类库带 `Password=` 会**打不开**（错误码 26）。这是刻意的失败而不是静默继续。处置：去掉 `Password=`（改用文件系统权限保护），或用 `sqlcipher_export()` 迁移
  - ⚠️ 样例配置长期都是 `Password=123456`。加密真正生效之后，这个口令就是唯一防线 —— 比"不加密"更危险地给人安全感，实际部署务必换成高熵口令
- **分析器基线只命中两条，两条都修了**（SEC-17）。`CA5359` 指向客户端 gRPC 通道**无条件**跳过证书校验（注释写着 "dev/demo 用"）—— 那不是风格问题，是把"连的是不是自己的网关"这个判断关掉了；现在默认正常校验，只有 `Gateway:Registration:AllowUntrustedGrpcCertificate=true` 才跳过。`CA5350` 指向 TOTP 的 HMAC-SHA1，那是 RFC 6238 规定的，换掉会静默废掉所有已注册的验证器，用带理由的 `SuppressMessage` 保留。基线归零后把整个 Security 分类提升为 `error`
- **插件沙箱不做，改为契约化**（SEC-18）。.NET 里没有可靠的进程内沙箱，`CollectibleLoadContext` 只解决卸载。安装对话框加显式警告（文档只有在意的人会读，对话框是唯一能改变决定的位置），并把"插件 = full trust、不能当隔离边界"写进 `docs/plugins/plugin-compatibility.md`

其余各条：插件 id 白名单 + 目录边界用尾随分隔符比对（`/plugins-evil` 不再被当成 `/plugins` 的子目录）；`2fa/*` 收紧为 admin-only 且 `setup` 由 GET 改 POST；转发头改由 `IStartupFilter` 注册并清空框架默认信任的 loopback；密码改 PBKDF2-SHA256 210k 自描述格式；会话改 `HttpOnly` cookie 并**移除**响应体与 localStorage 里的 token（两者并存会让前者完全失效）；登录加双键滑动窗口限流；补 CSP/XFO/XCTO/Referrer-Policy/HSTS；Webhook 出站分三段校验（字面量、逐次尝试、关闭自动重定向）；日志与响应里的 URL 凭据脱敏；认证/授权/插件/2FA 全部写审计；JWT 校验 `alg` 头并加 `jti` + 吊销表让 logout 真正生效；凭据比较统一走 `FixedTimeEquals`；TOTP 加 `lastAcceptedCounter` 高水位防重放；密钥文件在 Unix 上 `0600` 且**读取时也收紧一次**。

### 📌 本版概览

**多环境配置隔离（原 v2.6 第 1 项，三批全交付）** —— v2.6 的第一项「多环境配置隔离」拆成三批，现在三批都在本版里 —— 环境隔离与切换、环境间 diff、两步提升与审批。核心是一条边界：**网关转发哪个环境是启动时定死的，切环境只切"你在编辑哪个"**。这条边界不是设计洁癖 —— 如果切面板会改转发，那"打开 staging 看一眼"就等于把生产流量切走了。

**平台化收尾（原 v2.6 其余五项）** —— RBAC 三角色、Docker/Helm/`dotnet new` 三件套、管理 API 的 OpenAPI 文档、插件市场（模板 + 兼容矩阵 + 契约测试 + 面板页）、WAF 规则引擎的五种动作。这一批的共同点不是"又加了几个功能"，而是把前面几年堆起来的东西**补上对外承诺**：谁能改、怎么装、怎么对接、怎么写插件。顺带挖出两个"配置好了但从不执行"的坑（压缩插件的中间件、外部插件的代理管线）和三个界面缺陷（空白面板、英文兜底串、读不懂的追踪页），见下方对应条目。

**灰度与可观测（原 v2.5 六项）** —— v2.5 六项全部交付 —— 插件扩展点、权重灰度、灰度应用与自动回滚、指标告警阈值、OpenTelemetry 追踪、故障注入。核心是一条新策略 `Weighted`：按节点权重**精确**分流，配合 `X-Gateway-Canary` 头可以先把一个真实用户钉到新版本验证，再动权重；在此之上是一台靠指标说话的灰度状态机，观察窗口内错误率超阈值就自己退回去；再往外一层是阈值告警，负责在**没有人盯着灰度**的时候把 5xx 率、P99、熔断开闸叫出来。最外层是链路追踪 —— 告警说"错了多少"，span 说"错在哪一跳"。最后是故障注入：前面这些能力都是"等真故障发生时才验证"，而它可以主动把故障造出来，把重试、熔断、告警、自动回滚一次性演练一遍。

**写路径护栏下沉（原 v2.4.1）** —— 2.4.0 的预检与版本号只覆盖了 Dashboard 表单，而真正危险的写入（客户端自注册、批量导入、历史回滚、AI 助手）全部绕过了它们。本版把护栏下沉到核心库，让所有写路径共用同一条闸门，并补上 2.4.0 承诺但未兑现的三项。

**生产化底座（原 v2.4.0）** —— 不再只是"装得上"，而是"敢上线"。核心是把过去只在请求时才暴露的失败提前到保存时，并让指标出得去。

### 🚀 新增

- **OTLP 导出鉴权：三个来源，只回脱敏值**
  - `Headers` 以前只能手写配置文件，面板连"有没有配鉴权"都不显示 —— 而企业后端（Grafana Cloud / SigNoz / 商业 APM）几乎都强制鉴权，于是只剩"关掉导出"或"裸传"两个坏选择。后端其实一直支持，缺的只是出口
  - **三个来源按 header 名逐项合并**：`Gateway:Dashboard:Tracing:Headers`（配置）> `Data/tracing-auth.json`（面板）> `OTEL_EXPORTER_OTLP_HEADERS` / `..._TRACES_HEADERS`（标准环境变量）。最后一项是关键：容器/K8s 部署早已把 token 放在那里，要求改写成项目自定义的嵌套键，只会让人得出"这网关不支持鉴权"的结论
  - **接口永不返回明文**：只回 `Basic ****cFFxb1Y=`，保留 scheme 与末 8 位供区分两把 token。提交脱敏值本身会被拒绝，否则会把可用凭据替换成字面量 `****`
  - **留空 = 不使用面板覆盖**，不是"把凭据清空"：编辑器会提交每一行，没填的行必须回退到配置或环境变量，否则改一个 header 会静默丢掉其余来源
  - **写入独立文件而非改写 `appsettings.json`**：不重排格式、不丢注释，Linux 下权限 `0600`。admin-only，与 webhook 目标同级 —— 改导出目标等于改"数据发给谁"
  - **协议不再静默退化**：以前只认精确的 `http/protobuf`，`httpprotobuf`/`http-protobuf` 全部落进 gRPC 分支，症状是"collector 连不上"，把人引向网络排查。现在归一化 `-`/`_`/`/` 与大小写，仍不认识的值让导出器启动失败、原因显示在追踪页 —— 降级但可见，而不是拖垮转发面
  - 值里含换行直接拒绝：否则可以借此向每次导出注入任意 header
  - **`http/protobuf` 的 `/v1/traces` 改由网关补齐**。导出器 1.15.3 **不会**追加这个尾段 —— 本机用假收集器抓包确认，Endpoint 是什么路径就打到什么路径。而绝大多数 OTLP 教程仍按"SDK 会自动补"来写，照抄会少一段、后端返回 404，再被误读成"token 不对"。现在 `:4318`、`:4318/v1/traces`、`:5080/api/default` 三种写法都落到正确地址，gRPC 模式不做此处理（路径会拼坏方法名）
  - **启动日志打印最终导出地址**（`OTLP export URL: ...`）。导出失败对业务是静默的，"请求到底打到哪个 URL"是这类问题里第一个要确认的事实
  - **面板里来自配置文件的 header 变成锁定行**。配置文件优先级最高，在面板改它只会得到一个"保存成功但没生效"的假象；现在该行灰显不可编辑，并提示去改 `appsettings.json`
  - **示例配置里不再放真实凭据**。`samples/SampleGateway/appsettings.json` 是入库文件，原来直接写着某台 OpenObserve 的地址与 Basic 凭据 —— 提交一次，凭据就永久留在 Git 历史里，删掉当前行也救不回来。现在该文件只留 `http://openobserve:5080/api/default` 与 `Basic <base64(...)>` 占位符，真实值移入 `appsettings.Development.json`（`.gitignore` 已忽略）
  - 附 `docs/observability/tracing.md`：优先级说明、路径补齐规则、OpenObserve（`/api/{org}` + `organization` header）等各后端的具体填法、凭据该放哪里、以及端口错配这类最常见的坑

- **导出连通性自检：「页面有数据」不再等于「导出正常」**
  - 起因是一个真实误判：Endpoint 配成一个解析不了的地址，追踪页照样有数据。原因是同一个 provider 上的两条独立分支 —— 列表由 `RecentTraceProcessor` 喂内存缓冲（无条件挂载），导出走 OTLP exporter（仅 `ExportEnabled` 时挂）。导出失败是异步的、重试后静默丢弃，错误进 EventSource 而不进日志，于是**唯一能用的判据只有去后端查**，而那时已经过去很久了
  - 追踪页「数据出口」列新增「测试」按钮（admin-only），把**当前生效的** Endpoint 与鉴权头真实发一个空 OTLP 批次，结果直接显示在地址下方：`HTTP 200 OK · 74 ms · 2 个鉴权头`，或失败分型 + 原始异常 + 响应片段
  - **401 与 404 被明确区分**。这两种过去都表现为"连不上"，而它们的处置完全相反：前者换凭据，后者补路径（HTTP 漏 `/api/<org>`，或 gRPC 把路径塞进了 Endpoint）
  - **401 再往下分一层：`WWW-Authenticate` 原样显示**。起因是拿 OpenObserve 的 gRPC 入口配 Basic 凭据，得到一句 `401 Unauthorized`，看着就是"密码错了"。实测那个路由回的是 `WWW-Authenticate: Bearer realm="openobserve"` —— 它要 Bearer（ingest token），**不收 Basic**；同一把凭据打 `/api/default/v1/traces` 却是 200。现在这类结果标 `auth-challenge` 并把挑战头显示出来，意思是"凭据可能是对的，这个入口不接受这种凭据类型"。少了这一条，人会去轮换一个本来没问题的密码，然后仍然 401
  - **gRPC 不能只看 HTTP 状态码**：凭据被拒时 HTTP 仍返回 200，真实结果在 `grpc-status` trailer 里。非 0 一律判失败，否则就是这个探测本身最该避免的假绿灯
  - **gRPC 先做 TCP 预检**。`RequestVersionExact` 会把**所有**失败报成同一个 `VersionNegotiationError` —— 端口关闭、域名不存在也一样。实测确认后加了一步纯 TCP 连接用于分型，否则"主机名写错"会被显示成"协议不匹配"
  - 顺带查清了一台只开 5080 的 OpenObserve：5080 能连但不接受明文 HTTP/2，5081 无监听。也就是说它的 gRPC 根本不可用、只有 OTLP/HTTP 能走 —— 这类结论以前只能靠反复试
  - 新增 `POST api/tracing/verify`（admin-only，与改导出凭据同级：它会使用该凭据并让网关代发一次请求）。**接口不接受调用方传 URL**，目标永远是配置里的 Endpoint，返回的地址会剥掉 userinfo
  - 探测请求包在 `SuppressInstrumentationScope` 里，否则"测试导出"这件事本身会在列表里长出一条 span 冒充网关流量

- **WAF 重写为声明式规则引擎**（`Aneiang.Yarp.Plugin.Waf` 2.0）
  - **5 个硬编码检查器 → 一条有序规则管线**。每个规则 = 可选的**内置检测器**（`sql-injection` / `xss` / `path-traversal`）+ 可选的**条件集合**（用来限定检测范围）+ 一个**动作**。条件与检测器同时存在时是"在这个范围内跑这个检测"，只有条件时条件本身就是规则 —— 所以"只对 /api 开 SQL 检查""拦掉 UA 是扫描器的请求""给合作方打标签"都不需要改代码
  - **条件用一个形状覆盖全部来源**：`source`（method / path / query / body / header / cookie / userAgent / ip / contentType / bodySize）× `op`（equals / contains / prefix / suffix / regex / cidr / greaterThan / lessThan），再用 `matchMode: all | any` 组合。没有"每个来源一个字段"那种爆炸式的配置面
  - **五个动作**：`block`（拦截）、`allow`（停止求值并放行）、`log`（只记录）、`tag`（写 `X-Waf-Tag` 给上游）、`rate-limit`（同一个请求打太多次就限速）。`allow` 让白名单能表达成规则，而不是只能靠 IP 名单
  - **`rate-limit` 是闸门，不是流量整形**。配额是"同一个规则命中太多次就别再来了"（`limit: 5, window: 1m, partition: ip`），**没有队列** —— 把攻击者的请求排队只会把他们的洪水变成我们的延迟。窗口用**固定**而不是滑动：滑动的目的是"只要长期均值没超就放突发过"，而闸门存在的意义就是拦住突发；固定窗口还让 `Retry-After` 是精确值（秒级取整，最小 1）
  - **被拒绝的请求不延长窗口**，否则闸门会退化成永久封禁
  - **配额桶与限流插件完全隔离**。路由配额是对客户的承诺（"1000/min"），WAF 配额是对行为的判断（"五次失败尝试"）；共用键空间意味着被拦的请求会悄悄吃掉客户的合法额度。共享的只有语法（`30s` / `1m` / `1h`）
  - **裸数字在校验期就被拒**。`TimeSpan.Parse("300")` 是 300 **天** —— 把"写错的单位"放大成"三个月封禁"。`300` 在交给 `TimeSpan` 之前就被拦掉，规则丢弃并记 warning，不静默按毫秒或秒猜
  - **`rate-limit` 允许没有检测器和条件**。其他动作的"空规则"会被丢弃（它什么都不会做），但一个无条件的全局限速是合法且安全的 —— 它自带一个明确的数字
  - **`mode: monitor`**：同一套规则照常运行、照常记日志，但一条都不拦。上线一条新规则的正确顺序是先观察它命中了哪些真实请求，再决定要不要拦 —— 没有这个模式，第一次发现误报只能是在用户投诉之后
  - **拦截响应可定制**：状态码、Content-Type、响应体、附加响应头，规则还能单独覆盖状态码。默认在 `X-Waf-Rule` 里写明是哪条规则命中 —— 拿到一个 403 时第一个问题永远是"哪条规则干的"
  - **正文检查按内容和大小门控**：只扫 `text/*`、JSON、XML、表单、GraphQL（含 `+json` / `+xml` 后缀），`multipart/*` 与二进制类型直接跳过；超出 `maxBytes`（默认 64KB，上限 1MB）的请求体**不截断扫描**，直接跳过 —— 截断扫描对大上传只有成本没有覆盖
  - **不再全量缓冲请求体**。原先 `EnableBuffering()` 不带任何上限，为了扫前 100KB 会把整个上传落到临时文件；现在按已声明的 `Content-Length` 精确分配，只在内存里缓冲，读完立即复位供后续管线读取
  - **配置错误不会关掉整个防火墙**。未知字段忽略（不再是 `Disallow` 导致整条绑定反序列化失败），坏正则 / 未知动作 / 未知检测器只丢弃**那条规则**并记一条 warning，其余规则照常生效
  - **零迁移**：旧的 `enableSqlInjectionDetection` / `ipWhitelist` / `maxRequestBodySize` 等字段全部保留，`rules` 为空时自动合成原来的三条内置规则。IP 与大小校验作为前导始终生效，`allow` 规则**不能**绕过 IP 封禁
  - 内置检测器同时重写：关键词不再单独命中，只匹配有攻击形状的构造（`union select`、`; drop table`、`select … from` 的列清单形式、引号后的 `--`、`sleep()`、`information_schema` 等）
  - **Dashboard 换成规则卡片式编辑器**。原来通用的 schema 表单把 `rules[] → conditions[]` 摊成两层「Item 1」，配一条规则要先面对 14 个字段。现在一条规则是一张卡片，标题直接读成一句话（`拦截 SQL 注入（路径 前缀 /api/）`）。首屏只有四处可改：检测什么、在哪里比、比什么、怎么处理；`tag` 只在打标签时出现，`statusCode` 只在拦截时出现，`matchMode` 只在两个以上条件时出现，`header` / `cookie` 名只在需要它的来源上出现。操作符按来源过滤（路径条件上没有 `cidr`，UA 条件上没有 `greaterThan`），值控件跟着变（方法带建议、大小带字节换算、CIDR 校验格式、正则用等宽字体）。`id` / `name` 折进「标识与名称」—— 规则能跑之前没人需要先给它起名。新增六个模板（SQL 注入 / XSS / 路径穿越 / IP 网段 / 频率限制 / 仅观察）、规则上下移与复制、拦截/放行/记录/打标签/限流五色左边线。限流卡片把「次数 / 窗口 / 按 / 状态码」排在一行，标题读成 `限流 5 次 / 1m（按 IP）`；切走动作时残留的 `rateLimit` 会被清掉，避免一个不生效的配置留在 JSON 里误导人
  - **双轨制在界面上被讲清楚**。`rules` 非空时三个内置开关本来就不参与检测，但旧表单照样显示、照样可点，等于让人以为关掉 SQL 检测就安全了。现在规则非空时它们变灰并注明原因，另给一个「转成可编辑规则」把开关变成三条真实规则（顺序与编译器一致：路径穿越 → SQL 注入 → XSS），行为不变但从此可见可改
  - **面板校验比后端严，因为后端的容错是静默的**。编译器对写错的条件是「丢弃那一条 + 记 warning」，所以空值的条件不会让保存失败 —— 它会悄悄消失，而一条本该只作用于 `/admin` 的规则就变成全站生效。面板因此在保存前拦住空值、非法正则、非法 CIDR、缺 Header 名的条件，并把问题逐条列在顶部
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
- **环境间差异与一键提升**（v2.6 #1b/#1c）
  - **`GET <prefix>/api/environments/diff`**：按字段渲染成 `Destinations: 2 → 5` 这样的可读文本，而不是 `-{...}+{...}`。只比较**会改变流量行为**的字段（路径/域名/方法/顺序/变换/策略/超时/请求体上限；LB 策略/会话保持/健康检查/目标列表），改个描述不算差异
  - **跨环境读靠"借作用域"，不靠切视图**。`StorageEnvironmentScope`（`AsyncLocal<string?>`）让一次请求读写指定环境的库，而面板视图不动 —— 切换环境会重载工作集并触发发布，副作用远大于"看一眼"
  - **两步提升**：`POST promote/prepare` 返回差异 + 问题清单 + **单次令牌**（5 分钟有效），`POST promote/apply` 必须带这个令牌。令牌用过即从字典移除，被拒绝的 apply 不能重放
  - **指纹哈希的是配置输入，不是渲染后的差异**。第一版用差异做指纹，测试直接证明它会漏：一个"新增"的路由在差异里不带字段明细，于是批准之后偷偷改它的内容，差异完全相同，提升照常通过 —— 审的是一份东西，应用的是另一份。现在 `ConfigFingerprint.Of(source, target)` 哈希两侧配置本身
  - **候选配置用自己的集群校验**。拿 staging 的路由去对 prod 的集群表校验，方向是随机的：既会拦下合法提升，也会放过非法提升。校验器只看"这一份配置自身是否成立"（空来源、重复路由 id、指向不存在集群的路由、永远匹配不到的路由、没有目标节点的集群、非 http(s) 的绝对地址）
  - `EnvironmentPromotionService` 只依赖三个窄接口（`IEnvironmentConfigStore` / `IEnvironmentReloader` / `IPromotionAudit`），测试不需要伪造发布与版本号机制
  - 面板顶部环境切换器旁新增对比入口，弹窗里选源/目标 → 看差异（新增/删除/修改分色，逐字段前→后）→ 确认（明写对线上流量的影响）→ 应用 → 重新加载
- **RBAC 三角色**（v2.6 #2，见 `docs/adr/ADR-004-rbac-three-roles.md`）
  - `viewer` / `operator` / `admin`，切分依据是**爆炸半径而不是资历**：只读者看得到一切、改不了任何东西；运维能改网关；管理员才能动"决定谁能改网关"的东西（装卸插件、通知目标）
  - **viewer 按 HTTP 方法拒绝**（任何非 GET/HEAD/OPTIONS），不是按路径白名单 —— 后者会随页面增加而失效：新加一个页面就出现一个新的写入口，而没人记得回去更新那张表
  - **operator 按路径拒绝**，且列表是默认允许：新增的运维端点自动可用。代价被显式接受 —— 反方向（运维被自己的面板挡住）每天都在发生
  - **角色走 JWT 的 `role` claim**，不在每个请求里查账号表。副作用是降权需要重新登录，而这恰好是权限收紧时想要的语义：不靠"等他下次刷新"来撤销权限
  - **`Users` 为空时退回原来的单账号行为**（按管理员处理），升级零配置改动。角色名打错按管理员处理并写日志 —— 不让面板起不来，也不静默降权
  - **逐账号 2FA**：`TwoFactorSecret` 缺省继承面板级配置
  - **403 带 `reason` 码**（`read-only` / `admin-required`），前端翻成本地语言。看到的是"当前账号为只读，不能修改任何配置"，不是一个裸 403 加一句英文
  - **面板按角色隐藏写操作**：`data-requires-write` / `data-requires-admin` 两个属性 + 一条 CSS 规则（不是 JS 扫描，所以页面后来渲染出来的工具栏同样生效）；顶部显示角色，只读账号上方常驻一条说明。环境切换器整体隐藏 —— 切换环境会重新发布，它是写操作而不是筛选器
  - **无法归属的身份一律视为隐式管理员**：认证关闭、API Key、自定义委托。没有账号可归属时假装有角色，只会锁死存量部署；`Implicit` 标记一路传到前端，只为了让只读横幅不在一台没开认证的机器上谎报
- **部署物三件套**（v2.6 #3）
  - **`dotnet new aneiang-yarp`**（`Aneiang.Yarp.Templates`）：一条命令生成能跑的网关 —— YARP、SQLite 存储、面板已经接好，自带多阶段 Dockerfile 与「哪些是自动的、哪些必须自己改」的 README。`--Framework` 选 `net8.0` / `net9.0`。模板引用的是**打包产物**而不是源码，所以它验证的正是用户拿到的东西
  - **`deploy/docker/Dockerfile`**：官方参考镜像（跑本仓库的 `SampleGateway`）。非 root（uid 10001）、`HEALTHCHECK` 打 `/health`、连接串指到 `/app/data` + `VOLUME`、`curl` 只为探针而装（aspnet 基础镜像既无 curl 也无 wget，探针命令不存在时的"永远不健康"比没有探针更糟）。**它不是给人 `FROM` 继承的基底镜像** —— 网关是你自己的项目
  - **`deploy/helm/aneiang-yarp`**：Deployment / Service / gRPC Service / ConfigMap / Secret / PVC / Ingress / ServiceAccount / NOTES，探针、资源、安全上下文都有默认值
  - **chart 会拒绝渲染 `replicaCount > 1`**（除非显式 `allowMultipleReplicas: true`）。配置存储是一个 SQLite 文件，两个副本就是两份互不知道对方的路由表 —— 与其渲染出来再让运维在生产上发现"刚保存的路由不见了"，不如在 `helm install` 时就报错并说明原因
  - **`strategy: Recreate`**，不是 RollingUpdate：ReadWriteOnce 的卷不能让新旧 pod 同时挂载，滚动更新会卡在 Multi-Attach
  - **gRPC 单独一个 Service**（`appProtocol: kubernetes.io/h2c`）：很多 ingress controller 按 Service 决定后端协议，把 h2c 和 HTTP/1.1 放同一个 Service，要么被忽略，要么把整条链路降级
  - **`checksum/config` 注解**：没有它，改 values 再 `helm upgrade` 只会更新 ConfigMap，跑着的 pod 还是旧路由
  - **凭据随机生成并 `keep`**：`auth.jwtPassword` 留空时安装过程生成随机密码写进 NOTES 并可从 Secret 取回，Secret 与 PVC 都带 `helm.sh/resource-policy: keep` —— `helm uninstall` 不该顺手删掉所有路由、也不该让所有人掉线
  - **`auth.users` 按下标注入环境变量**（配置绑定在环境变量里没有数组语法），注释写明下标必须从 0 连续，中间空一个会静默截断列表
  - **CI 新增 `deployables` 作业**：把本 commit 打成包喂给模板并真的编译它、`helm lint` + 渲染 + 断言关键对象齐全 + 断言 `replicaCount: 2` 的守卫仍然生效、`docker build` 不推送。三个产物都是"装得上但用不了"的高发区，而这类问题只在用户那里暴露
- **管理 API OpenAPI 文档**（v2.6 #5）
  - **文档从 `EndpointDataSource` 生成，不是手写契约**。面板的控制器**就是**这个 API；手写的 spec 在有人加第一个接口的那天就开始漂移，而一份会撒谎的文档比没有文档更糟 —— 它会让第三方的集成在生产上失败
  - **按路径前缀收口，不按命名空间**：插件自带控制器挂在同一个 `<prefix>/api/` 下。第一版按 `Aneiang.Yarp.Dashboard` 命名空间过滤，结果 `circuit-breaker` 的三个接口凭空消失；集成方按文档写完，调过去 404
  - **schema 由 CLR 类型推导，推导不出来就给宽松 schema**。`JsonElement` / `JsonNode` 参数只标注「原生 YARP 配置对象，结构见文档『配置参考』」并留一个空 schema —— 编一个看起来精确的假 schema 是主动制造故障。类型图有深度上限与环检测：环上返回空 schema，宁可"任意"也不要栈溢出
  - **`[FromBody]` 必须先于"注入服务"判定**。真实控制器里 48 个 `[FromBody]`，第一版只有 8 个出现了 `requestBody`：判定把 `Aneiang.Yarp.*` 命名空间一律当服务，而请求模型全在这个命名空间下。修好后 37 个 —— 剩下 11 个是 GET 上的可选模型或本身就是原始 JSON
  - **`required` 按 MVC 的规则算，不是"引用类型即可选"**：`[FromBody] Model body` 是必填，`Model? body` 或带默认值才可选。契约写着"可选"的必填体，调用方会跳过它然后收到 400
  - **路由模板兜底**：`AttributeRouteInfo` 缺失时取 endpoint 自己的 route pattern，约定路由的控制器因此也进文档，而不是被静默丢掉
  - **`<prefix>/api/openapi.json`**：OpenAPI 3.1，95 条路径 / 110 个操作，`components.securitySchemes` 同时给出 bearer 与 api key，403 的说明里写明 `read-only` / `admin-required` 两个 reason 码
  - **`<prefix>/api-docs` 是一个零依赖的独立页面**：不引 CDN、不引 Swagger UI —— 一个连自己都解释不了的控制面，恰好会在最需要它的时候（离线、内网、CDN 被墙）什么都解释不了。页面按 tag 分组、可过滤、逐操作展开参数/请求体/响应，并且自带「返回控制台」入口
  - 侧栏「系统管理」新增 **API 文档** 入口；页面无 Layout 是刻意的：文档页要能单独发给对接方
  - 不引 `Microsoft.OpenApi` / Swashbuckle：整份文档就是一个 JSON 对象，为此背上一个自带版本策略的模型库不划算
- **插件市场三件套**（v2.6 #4）
  - **`dotnet new aneiang-yarp-plugin`**：一条命令生成能装进宿主的外部插件。`dotnet publish` 出来的目录直接拷进 `plugins/` 就能被识别 —— `plugin.json` 与程序集、依赖一起躺在那里
  - **`plugin.json` 是生成的，不是手写的**。构建时由 `plugin.manifest.json` 渲染，五个带 `$...$` 的字段来自 csproj 属性：`entryAssembly` ← `$(AssemblyName).dll`、`entryType` ← `$(RootNamespace).XxxEntry`、`version` ← `$(Version)`。理由是这三个字段错了**只有别人装上去才会暴露**，改成推导就不可能留下一个指向旧 dll 的清单
  - **模板不把契约程序集打进插件目录**（`ExcludeAssets="runtime"`）。宿主始终用自己的那一份；否则就是两个 `IGatewayPlugin` 类型互相不认，报出来的错没人看得懂
  - **`docs/plugins/plugin-compatibility.md`**：内置 11 个插件的作用域/能力/`order`/schema 版本/依赖矩阵，加上 `plugin.json` 运行时校验表、外部插件的"能做 / 不能做"对照表、版本演进记录
  - **`PluginContractTests`（12 个）**：钉的是那些**坏了也不报错**的不变量 —— id 格式与唯一性、清单与实例的 id/名称/版本一致、版本是语义化版本、`order` 互不重复（重复会让管线顺序取决于字典遍历顺序）、schema 是合法 JSON 对象、声明了 `LoadBalancingPolicy`/`Transform` 的插件必须实现 `IGatewayPluginHostServices`、依赖能在已装插件里解析、以及兼容矩阵与代码里的插件集合**双向一致**（文档少一个或多一个都红）
  - CI 的 `deployables` 作业新增一步：实例化插件模板 → `dotnet publish` → **断言渲染出来的 `plugin.json`**（id / entryAssembly / entryType / capabilities 四处）→ 断言契约程序集**不在**插件目录里。插件模板的失败方式和网关模板不同：它编译得过，然后宿主拒绝加载
- **两处"配置好了但从不执行"的修复**（跟 #4 一起暴露出来的）
  - **代理管线顺序改为读 manifest `order`，不再用宿主里写死的 8 个内置 id**。写死的列表决定谁能参与，意味着不在列表里的插件配好了、绑好了、面板上也亮着，但中间件永远不被调用。`compression` 正好在这个洞里：它的 `CompressionMiddleware`（按路由、可配 `minResponseSize`/`mimeTypes`/`level`）从来没跑过，实际生效的是面板层那个全局 `UseResponseCompression()` —— 也就是说压缩插件的绑定是纯装饰。外部插件更是全都进不去
  - 修好后顺序仍是原来那个（rate-limit → circuit-breaker → compression → request-retry → response-cache → proxy-log → traffic-metrics → cluster-metrics → fault-injection），因为内置插件的 `order` 本来就是照这个排的；变化的只是 `compression` 归位、以及对第三方插件打开
  - 不会双重压缩：ASP.NET Core 的响应压缩在 `Content-Encoding` 已存在时跳过，而插件的中间件在更内层先执行
  - **`IGatewayPlugin.ConfigureMiddleware` 的注释改成实话**：它只对内置 `waf` 生效。应用级管线在启动时构建完成，插件是之后才启用/重载的，所以没法再往里加中间件；写在里面的中间件是静默空操作。模板样例原本就写在 `ConfigureMiddleware` 里 —— 幸好按"模板必须验证可用"的规矩跑了一遍真实网关才发现
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

### 🎨 界面

- **插件市场页（`<prefix>/plugin-market`）** —— v2.6 #4 的后端（`POST api/plugins/install`、`DELETE api/plugins/{id}`、`{id}/upgrade`）上一批就有了，但**没有任何页面调用它们**，`install` 连 `DashboardApi` 里的封装都没写。这一批补上：
  - 自托管网关没有远端商店，也不该有 —— 面板没法验证一份它下载不到的代码。所以"市场"在这里只做三件真事：**当前装了什么（含来源）**、**从本机目录安装/升级/卸载**、**外部插件必须满足的契约**
  - 卡片而不是表格：这里要回答的是"这东西干什么"，一句话说明在表格列里活不下来。内置 11 个 + 外部插件同屏，来源用徽标区分（内置 / 外部），外部插件才出现升级与卸载
  - 概览条四个数（内置 / 外部 / 已启用 / 待绑定）回答"装了什么"，之后再问"哪一个"；筛选段上直接挂计数，不必先切筛选才知道有没有外部插件
  - **三种空状态给三条不同的下一步**：注册表为空（去看启动日志）、还没装外部插件（给出 `dotnet new` / `publish` 三步命令）、筛选无结果（清空筛选）。合并成一句"暂无数据"就是让人以为页面坏了
  - 安装对话框**失败时不关闭**：目录里缺 `plugin.json`、TFM 不匹配这类原因只有服务端那句话说得清
  - 安装/升级/卸载标 `data-requires-admin`、启用开关标 `data-requires-write`，与 ADR-004 的角色边界一致（这三条在 `DashboardRolePolicy` 里本来就是管理员专属）
- **插件配置页的面板改为由注册表生成**。视图里原本手写了 10 个 `.pc-tab-panel`，而 JS 注册表里有 11 个插件 —— 漏掉的正好是 `fault-injection`。`renderConfigPanel` 找不到宿主就静默返回，于是点开是一整片空白，控制台没有任何报错。现在面板由 `REGISTRY` 逐个创建，注册表是唯一来源；万一容器仍不存在，标签上会出现一个红色 `!` 而不是一片安静
- **链路追踪页重构**。原来一屏里三件事混在一起，读起来像仪表盘而不是链路：
  - **状态带**分成三列，一列一个问题：**在跑吗**（状态徽标 + 服务名 + 配置节）、**数据去哪**（`grpc → http://…` 或「仅内存」+ 缓冲与捕获数）、**采样多少**（滑块 + 百分比 + 应用，说明就在控件下面）。原来是徽标、滑块和 5 个同级指标挤在一行，采样的解释离控件两行远
  - 补上 **`等重启生效`** 这个状态：`enabled=true` 但 tracer 还没起来时，"开启"和"在工作"确实不是一回事
  - **链路列表改成两行式并对齐**：第一行是请求，第二行是 `路由 → 集群 → 目标` 的跳链（这是要跟其它页面互相印证的东西；原来五个 chip 在同一行里抢位置）。列头与行共用一套 grid 模板，**耗时右对齐并配一条相对长度条**，慢的那一条一眼能看出来，不用逐行比数字
  - **瀑布图有时间轴**：0 / ¼ / ½ / ¾ / 总量五档刻度与 25% 网格线，列头写明 Span / 时间分布 / 耗时，底部有颜色图例。原来是一堆没有参照的彩色条
  - 追踪页的 60 行样式从 5k 行的布局文件搬进页面自己，类名统一加 `tr-` 前缀
- **五个「后端有接口、面板没有入口」的功能补了前端**。起因是一次全量对账：把 28 个控制器的 142 个端点与 `wwwroot/js` + `Views` 的引用做双向差集，发现有一批能力**能用 curl 调、在面板里点不到**。对一个控制面产品来说，"接口存在"和"用户能完成这件事"是两回事 —— 前者只是后者的必要条件。
  - **配置全量体检**（`GET api/config/integrity`）。路线图 §4.2 把它标成"已交付"，但它从来没有被任何页面调用过，唯一的使用方式是 curl。现在配置历史页有「全量体检」入口，报告按错误/警告分组，每条带**字段路径与修复建议**（`Routes.r1.ClusterId` → "这条路由会直接返回 503，请补建集群或改指向"），并给一个**直达出问题那条路由/集群**的按钮。没有这个按钮，报告只是一份"你有问题"的通知
  - **深链定位**（`?focus=<id>`）。上面那个按钮要落到具体一行才闭环，所以路由页与集群页现在认 `?focus=`：等列表渲染出来（`MutationObserver`，不是固定 setTimeout），滚到那一行、加左侧高亮、4 秒后淡出，然后把参数从地址栏删掉。参数留着会让刷新重复高亮一个已经修好的条目
  - **熔断阈值**（`GET api/circuit-breaker/config`）。熔断页原来只有状态表，而状态表只能显示**已经评估过**的集群 —— 配了阈值但还没流量的集群在 `/status` 里根本不存在，看起来像"没配置"。现在下方多一张「熔断阈值定义」表，逐集群列出启用状态、失败阈值、恢复时间、半开探测次数、触发条件（失败率 ≥ x% · 样本 ≥ n · 窗口 n 秒）与计入的响应码；有实时状态的集群在行内叠加状态徽标，没有的显示「未触发」并解释原因
  - **策略预设的删除与应用**（`DELETE api/presets/{id}`、`POST api/presets/{id}/apply`）。预设原本只能"存"和"读"：建了删不掉，只能越积越多；选中预设只是把配置填进表单，还要再点一次保存 —— 于是"我选了预设"和"预设生效了"之间隔着一步，而那一步没人提醒。现在表单旁有「应用」（直接把预设写进当前绑定，跳过表单）和「删除」（预设是共享定义，删它不动任何在跑的绑定，所以确认框只说删哪一个，不说会坏什么）
  - **批量绑定启停与删除**（`POST api/plugin-bindings/batch-enabled`、`batch-delete`）。之前只有批量**创建**，没有批量**关/删**。插件配置页的绑定表加了行复选框、全选（带 indeterminate 状态）和一个"有选中才出现"的操作条；单条 toggle 会触发重载，所以选择状态存在 render 之外，否则点一次单条开关就把勾选清空了
  - **配置快照队列指标**（`GET api/config/snapshot/metrics`）。快照是后台异步写的，队列满了或丢弃了，时间线上唯一的症状是**某个版本不存在** —— 和"没人改过"长得一模一样。配置历史页因此常驻一条队列状态带（队列 / 已处理 / 失败 / 丢弃 / 容量），有失败、丢弃或队列打满时整条变琥珀色并明说"时间线中可能缺少版本"

### 🔧 架构改进

- 新增 `Modules/GatewayConfig/Services/ConfigurationValidator.cs`（`IConfigurationValidator`），写路径统一为"校验通过才落盘"
- 新增 `Infrastructure/Policies/GatewayPolicyCatalog.cs`，`AddGatewayPolicies()` 追加式注册（与宿主已有的 `AddAuthorization` / `AddCors` 共存），全部 `TryAdd`/`AddXxx(options)` 语义
- 新增 `Infrastructure/Observability/`：`GatewayMetricsCollector`（无 DI 依赖、线程安全、有界基数）、`GatewayMetricsMiddleware`、`GatewayMetricsRenderer`
- `ConfigControllerBase` 新增 `TryRejectStaleWrite()` 与 `HasDestinations()`（后者避免"首次建集群 + 建路由"时误报集群不存在）
- 新增**插件配置编辑器扩展点** `PluginConfigEditors`（`js/core/plugin-config-editors.js`）。通用 schema 渲染器遇到嵌套的「对象数组」时就只能输出「Item 1」，而 WAF 的 `rules[] → conditions[]` 是两层。现在插件可以自带编辑器接管整个表单：`renderGroupedFields` 先查注册表，命中就把 host 交给插件；`readSchemaForm` 认 host 上的 `__readConfig()`（返回完整 config 或 null）。两个调用方（Route/Cluster 的能力弹窗、插件中心的绑定弹窗）**不改一行**就都切换到新界面，以后其它插件也能按同一契约接入

### 🐛 修复

- **「故障注入」的插件配置页点开是空白**。视图里手写了 10 个 `.pc-tab-panel`，JS 注册表里有 11 个插件，缺的那个正是 `fault-injection`；`renderConfigPanel` 用 `document.querySelector` 找宿主，找不到就 `return`，于是标签在那儿、面板不存在、所有面板又被统一 `display:none` 掉 —— 页面上什么都没有，控制台也什么都没有。这类"静默返回"的失败比抛异常难查，因为没有任何一处报错。修法不是补一个 div，而是**把面板交给注册表生成**（`#pc-panels` 挂载点），顺手加了兜底标记
- **「备份数据库」按钮在中文界面显示英文**。`dashboard-settings.js` 用 `t('settings.config.backupBtn', 'Backup Database')`，而 `core.json` 里这个键从来没加过 —— 键缺失时 `t()` 落到第二个参数，于是中文界面读到的是英文兜底。同一个键在 2.3.0.40 的中英文包里都不存在，所以英文界面反而正确。补上中英双语，并加了测试防止再出现（见「测试」）
- **文件上传被 WAF 100% 拦成 403（Gitee #IKW95C，严重）**。SQL 检测的关键词正则里有 `\B--`，而 `\B` 是"非单词边界"、`-` 本身是非单词字符 —— 所以只要 `--` 前面是空格、`=` 或行首就会命中，而 multipart 的每一行 boundary 都是 `------WebKitFormBoundary…`，行首正好是换行。结果就是**任何走 WAF 路由的文件上传都返回 403**，`details` 里写的还是 boundary 本身。同一个正则也扫查询串，`?args=--verbose`、`?q=--help` 这类参数同样被拦
  - 第二个原因在中间件：读 body 的开关只看"有没有 POST/PUT/PATCH 和 Content-Length"，**不看 Content-Type**，PDF、图片都被当 UTF-8 文本做正则匹配
  - 第三个原因在缓冲策略：`EnableBuffering()` 不带上限，为了扫前 100KB 会把整个上传落到临时文件
  - 三处都在 WAF 重写里一并解决（见「新增 / WAF 重写为声明式规则引擎」）。顺带把"关键词单独出现就算攻击"这一整类误报也去掉了：`select a size from the list` 这种正常文案不会再被当成注入
- **启用 WAF 路由后的第一个请求可能被当成攻击拒掉（偶发 403）**。三个内置检测器用 `RegexOptions.Compiled`，首次匹配时才 JIT 编译，而这次编译的耗时**计入** 50ms 的匹配超时预算；超时按命中处理（安全边界），于是机器稍忙时第一个通过 WAF 的请求会返回 403，之后全部正常。表现出来的样子是全量测试并行跑时约 1/3 概率红一个用例、单独跑永远绿 —— 一开始容易被当成测试不稳定，实际是启动期的一个真实误拦。改为在类型初始化时预热三个正则，把一次性编译成本从"误拦一个请求"变成"启动慢一次"；预热失败也吞掉，否则静态构造函数抛异常会让整个类型永久不可用
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

  | 写入路径 | 改造前 | 现在 |
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

- 新增 `deploy/README.md`：三种部署方式的区别、共用约束（单副本、存储必须在容器外、认证默认开启、探针用业务端口）、Docker/Helm 用法与常用覆盖、以及一份部署后按顺序执行的验证清单
- 两个 README 各新增一节「部署（2.3.0.40）」/「Deployment (2.3.0.40)」，只讲三条命令和两条必读约束，细节指向 `deploy/README.md`
- 新增 `templates/Aneiang.Yarp.Templates`（`dotnet new` 模板包），内容自带 README：路由/存储/面板各在哪、怎么用 API 建路由、认证怎么改、生产环境要改哪几项、插件包的实际情况
- 新增 `docs/adr/ADR-004-rbac-three-roles.md`：三档角色的切分依据（爆炸半径而非资历）、为什么 viewer 按方法拒绝而 operator 按路径拒绝、为什么无法归属的身份按隐式管理员处理、`Gateway:Dashboard:Auth` 完整配置形态（含逐账号 `Role` 与 `TwoFactorSecret`）、以及三条明确写下的负面后果
- `docs/adr/README.md` 索引补 ADR-004
- 路线图 §0.1 / §6 v2.6 按实际状态更新（**v2.6 6/6**），并补「#1b/#1c 差异与提升、#2 RBAC、#6 rate-limit」复盘 8 条、「#3 部署物、#5 OpenAPI」复盘 10 条
- 新增 `docs/plugins/plugin-compatibility.md`：11 个内置插件的作用域/能力/`order`/schema/依赖矩阵、`plugin.json` 运行时校验表、外部插件"能做 / 不能做"对照表（含 `ConfigureMiddleware` 不生效这条）、版本演进记录、自测命令
- `templates/Aneiang.Yarp.Templates/content/AneiangYarpPlugin/README.md`：`plugin.json` 为什么是生成的、清单里三个容易填错的字段、怎么加配置、版本兼容
- `README.md` / `README.zh-CN.md` 新增「Plugin development （2.3.0.40）」/「开发插件（2.3.0.40）」与「Management API reference」/「管理 API 文档（2.3.0.40）」两节
- `README.md` / `README.zh-CN.md` 新增「WAF (2.3.0.40)」/「WAF 防火墙（2.3.0.40）」：规则模型、条件矩阵、五种动作（含 `rate-limit` 的固定窗口与独立桶语义）、monitor 上线流程、正文检查边界与完整配置示例
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
- **路线图与代码对齐一次**：§4.2 灰度、§5 RICE 表五行（权重路由 / 指标告警阈值 / WAF / 多环境 / RBAC）仍标着 ⬜/🔵/🟡，实际早已交付；测试数在文档里同时写着 207 / 158 / 117 三个数，统一为当前真实值；「管理页面 14 个」补上插件市场与 API 文档页后是 16 个；`ProxyPluginOrder` 已被 manifest `order` 取代，两处引用改回现行机制（第三处保留，因为它正是记录这次替换的复盘）
- **i18n 重复键清零**：中英各有 46 个键定义在两个域文件里，`DashboardI18n.LoadLocale` 按固定顺序合并、后者覆盖前者，于是「改哪一份生效」取决于顺序 —— zh 有 34 个、en 有 28 个键的两个副本值不同（`diff.title` 一份写「配置对比」一份写「配置差异对比」，屏幕上是后者）。按命名空间归属合并（`webhook.*` → `webhook.json`、`diff.*` / `history.*` → `config.json`、`common.*` / `settings.*` / `index.*` / `layout.*` → `core.json`），**每处都取当前实际生效的那份值**，所以清理前后屏幕上的字一个不变；顺带修正语言选择器：两个语言项一律用各自的语言书写（简体中文 / English），原先 en-US 包里 `settings.locale.zhCN` 曾被写成中文

### ✅ 测试

- **`DashboardEndpointCoverageTests`（2 个）—— 这条测试是这次返修的直接产物**。起因是"后端有接口、面板没入口"被证实不是个别现象（一次差集就找出 5 类 6 个功能），所以除了补前端，更要把判据固化下来：扫描全部控制器里以 `/api/` 开头的端点，要求每一个在 `wwwroot/js` + `Views` 的源码里都有调用点。
  - 匹配必须能被"人真正写出来的 URL"满足，否则它会用一堆假阳性把自己变成噪音。前端既写字面量，也写 `var API = '/api/canary'; API + '/rollouts/' + id + '/abort'`，还写模板字符串。做法是先把 `var X = '/api/...'` 的前缀内联进同文件，再抹掉引号与 `+`，让拼接读成一串字符；模板里的参数位是 `[^\n]{0,160}?`，**不跨行** —— 跨行通配会把相隔两行的两条 URL 接起来，那是最容易骗过这类测试的写法
  - 第一次跑报了 15 个 canary / alerts / environments 端点，全是"前缀变量 + 拼接"写法，说明是匹配精度不足而不是功能缺失。这条用例有一半价值在于它**必须不误报**
  - 白名单只放两类，且各有理由：`/api/gateway/*` 是被代理服务调用的客户端自注册 SDK（本仓库的 UI 不该调它），`/api/plugin-resources/{pluginId}`、`/totals`、`GET /api/presets/{id}` 是冗余读（集合端点已返回同样字段）。另有一条用例断言**白名单不腐烂**：条目对不上真实端点就红，否则它只会越长越大
  - 还有一条下限断言（`>= 90` 个端点）：解析器写坏时得到一个空集合，会让"全部通过"成为最危险的假绿
- 真实网关（SampleGateway，非单测）复核了这次补的每个入口：`GET api/config/integrity` 200 且返回 `{ok, errors[], warnings[{severity,field,message,hint}]}`（实测报出一条"集群未被任何路由引用"的警告，正是这个接口该说的话）；`GET api/circuit-breaker/config` 200，返回 `baidu` 集群的 8 个阈值字段；`GET api/config/snapshot/metrics` 200（队列 / 已处理 / 失败 / 丢弃 / 容量）；`GET /aneiang/history`、`/circuits`、`/plugin-center` 全 200，HTML 里能查到新按钮、报告模态，以及新增的中英键（`history.integrity.*` 13 个、`history.queue.*` 6 个、`circuit.thresholds.*` 12 个、`pluginPage.batch*` 9 个）
- 部署物单独做了一次端到端验证（非单测，本机无 docker/helm，这部分由 CI 的 `deployables` 作业覆盖）：
  - `dotnet pack` 模板包 → `dotnet new install` → `dotnet new aneiang-yarp -n MyGateway` → 用本 commit 打出的本地包源 `dotnet build`：**0 警告 0 错误**，`AneiangYarpGateway` → `MyGateway` 在文件内容与文件名上都被正确替换（`ENTRYPOINT ["dotnet", "MyGateway.dll"]`、`RootNamespace`）
  - 真的把生成的网关跑起来（`dotnet MyGateway.dll`，Production）：`/health` `/ready` `/aneiang/login` 全 200；`POST /aneiang/login` 返回的 JWT 解出 `"role":"admin"`；`GET /aneiang/api/auth/status` 返回 `role=admin, isImplicitRole=false, canWrite=true, canAdminister=true` —— RBAC 的 claim 与状态接口在真实进程里跑通
  - 顺手发现两处文档不能说的话，已改：① 登录端点是 `POST <prefix>/login`，不是 `api/auth/login`；② 集群的 `destinations` 是**以名字为键的对象**，不是数组（写成数组会得到 400 加一句 JSON 路径错误）。另外核实了 `Aneiang.Yarp.Dashboard` 依赖**全部**内置插件包（配置插件需要它的 schema），所以"插件是可选包、只有用到才装"这句话只对不带面板的纯数据面成立 —— 模板 README 与 csproj 注释都改成了准确表述
  - **未验证**：`PUT api/config/routes/{id}` 之后立刻访问该路由仍返回 404（重启进程后同样），沙箱内无法核实是环境问题还是产品行为，已交由用户在面板里确认
- 用量 **635** 个（上一批 489）：新增 `EnvironmentPromotionTests`（16 个）、`DashboardRoleTests`（54 个）、`OpenApiDocumentBuilderTests`（18 个）、`PluginContractTests`（12 个）、`DashboardI18nTests`（6 个）、`DashboardEndpointCoverageTests`（2 个）、`TracingAuthStoreTests`（23 个）、`TracingEndpointTests`（8 个）、`TracingExportProbeTests`（22 个），`WafEngineTests` 补 9 个、`WafMiddlewareTests` 补 3 个、`DashboardRoleTests` 的 admin-only 用例补 1 个（`tracing/verify`）
- `TracingExportProbeTests`（22 个）里有两条是**做功能时才发现的**：`RequestVersionExact` 会把 DNS 失败、端口关闭、后端不支持 HTTP/2 全部报成同一个 `VersionNegotiationError`，所以 gRPC 分支必须先用一条纯 TCP 连接分型，否则"主机名写错"会被显示成"协议不匹配"；以及 gRPC 凭据被拒时 HTTP 状态码仍是 200，真实结果在 `grpc-status` trailer 里 —— 只看状态码的探测会给出假绿灯。两个用例都钉住了这两点
- `DashboardI18nTests`（6 个）是补界面时顺手加的，因为它当场抓到一个真缺陷：**中英两包键集合一致**（少一个键在运行时只会静默回落到另一种语言）、**占位符一致**（翻译漏掉 `{count}` 会渲染出一句缺数字的话）、**没有非预期的空白值**（`个` / `个生效中` 这类量词在英文里故意留空，白名单里只放这三个）、**视图与脚本引用的每个字面量键都存在**。最后一条会逐文件识别 `t(key) => __('malert.' + key)` 这种带命名空间前缀的包装、以及 `t('verdict' + record.verdict)` 这种拼接键，避免误报；它抓出了 `settings.config.backupBtn` 缺失
- `No_key_is_defined_in_more_than_one_domain_file`（1 个）是这次 i18n 清理的判据，不只是"清完留个记录"。它按域文件统计键的出现次数，任何一个键落在两个文件里就红。这条能立住的前提是**清理时保留的是屏幕上的值**：如果顺手把"更通顺"的那份留下，等于在没人知道的情况下改了界面文案；如果留下的是没生效的那份，界面就会立刻回退成旧文案。两种都属于"删重复删出了新 bug"
- `PluginContractTests`（12 个）里最值得留的一条是**矩阵与代码双向一致**：内置插件少写进文档是"集成方看不到"，文档多写一行是"集成方去装一个不存在的插件"。两边都钉，才能保证这份矩阵不是一份会过期的宣传材料
- `Orders_are_unique_across_plugins` 钉住一个很容易被忽略的耦合：代理管线顺序是 `(order, id)`，两个插件 `order` 相同仍然能跑，但实际顺序变成了"id 排序靠前的先执行"—— 加插件的人不会想到去查这个
- `OpenApiDocumentBuilderTests`（18 个）钉的是生成出来的契约本身：只有 `<prefix>/api/` 下的端点进文档（页面路由与外部前缀都不进）、`BasePath` 带尾斜杠也会被归一化（否则 `//api/` 让整份文档变空）、同路径多动词合成一个 path item、没有动词约束的端点被跳过（凭空写成 `get` 就是假契约）、无 XML 文档时摘要回落为 `Get Routes`、路由模板参数进 `path` 且必填、标量进 `query` 且可空即选填、`CancellationToken` 与无 `[FromBody]` 的复杂参数被丢弃、请求体由 CLR 类型推导（含枚举名、`array`/`additionalProperties`）、自引用类型不递归爆栈、原始 JSON 体留空 schema 并写明原因、403 描述里带两个 reason 码、整份文档能过一遍 JSON 解析器
- 其中一条是**回归守卫**：`Build_KeepsPayloadTypesDeclaredInOwnNamespaces` —— `[FromBody]` 必须先于"注入服务"判定，否则声明在 `Aneiang.Yarp.*` 下的请求模型全被当成服务丢掉
- OpenAPI 另跑了一遍真实网关（SampleGateway，非单测）：`GET /aneiang/api/openapi.json` 200 / 166 KB，**95 条路径 110 个操作**，`requestBody` 从修复前的 8 个涨到 37 个（48 个 `[FromBody]` 里剩下的是 GET 上的可选模型），插件控制器（`circuit-breaker` 等）在列；`GET /aneiang/api-docs` 200，按钮与提示文案完整（`POST /aneiang/login` 已正确插值），侧栏「系统管理 → API 文档」入口可点
- 插件模板也走了一遍**完整链路**（非单测）：`dotnet pack` → `dotnet new install` → `dotnet new aneiang-yarp-plugin -n DemoPlugin --PluginId demo-plugin` → `dotnet publish`（0 警告 0 错误）→ 把 output 目录整个拷进宿主的 `plugins/demo-plugin/` → 重启网关
  - 启动日志 `Plugin 'demo-plugin' state loaded from gateway_plugins: True` + `External plugin demo-plugin v1.0.0 loaded into a candidate runtime domain`
  - `GET api/plugins` 返回 **12** 个（11 内置 + demo-plugin），字段与 `plugin.json` 一致
  - 面板里启用后请求被代理的路由，响应头出现 `X-Plugin-Id: demo-plugin` —— 第一次跑**没有**这个头，正是这里挖出了上面那个"代理管线顺序写死"的 bug
  - 发布目录里**没有** `Aneiang.Yarp.Plugin.Abstractions.dll`，确认 `ExcludeAssets="runtime"` 生效
- `EnvironmentPromotionTests` 钉住的是"审的和应用的是同一份东西"：提升从**目标**的角度描述（`Destinations: 2 → 5` 而不是 `-{...}+{...}`）、新增/删除/修改分色归类、配置完全相同时 `Identical` 为真、指纹在两侧任一变化时必须改变（第一版用差异做指纹，正是这条用例把它打红的）、来源为空时拒绝、候选配置引用另一环境的集群时必须拦下、没有目标节点的集群必须拦下、相对地址必须拦下、prepare→apply 成功路径、来源在批准后被改动要 409、目标在批准后被改动要 409、未知令牌拒绝、同一环境提升到自己拒绝、被拒绝的 apply 不能重放
- `WafEngineTests` 新增的 9 个钉 `rate-limit` 的编译语义：正常配额可编译、缺配额按丢弃处理（不默认成 60）、无条件的全局限速**保留**（其他动作会被丢弃，这里不能）、`300` 这种裸数字被拒、未知分区被拒、`limit` 为 0 或负数被拒
- `WafMiddlewareTests` 新增的 3 个钉运行时行为：超配额返回 429 且 `Retry-After: 3600`（窗口剩余量，不是固定值）、不满足条件的路径不计数（否则一条只针对 `/login` 的限速会拦掉整站）、monitor 模式下记录事件但放行
- `DashboardRoleTests`（52 个）分成三段：角色名解析（大小写不敏感、`read-only`/`ops` 别名、未知值回落并报错、空值静默回落）；拒绝规则（viewer 三种安全方法通过、四种写方法一律 `read-only`；operator 改路由/集群/绑定/环境通过，装卸插件与改通知目标一律 `admin-required`，但**仍可读**这两处；admin 全通；`api/plugin-bindings` 与 `api/plugins/*` 不能混为一谈）；身份解析（认证关闭 / API Key / 自定义委托 → 隐式管理员；token 里的角色如实生效；**没有 `role` claim 的老 token 取账号配置的角色而不是默认管理员**；未知账号的老 token 保持可用；JWT 模式但拿不到 token → viewer；伪造签名的 token 不会变成管理员）
- 用量 461 个（上一批 387）：新增 `WafEngineTests`（57 个）、`WafMiddlewareTests`（17 个）。WAF 原先**一个测试都没有** —— 这也是那类误报能活到现在的原因
- `WafEngineTests` 有一半是**误报回归**：multipart 信封、PDF 头、`q=--help`、`select a size from the list`、`well -- known`、`/* not an injection */`、`{"promo":"it's a deal"}` 必须放行；`' OR '1'='1`、`admin'--`、`UNION SELECT`、`; DROP TABLE users`、`INSERT INTO`、`WAITFOR DELAY`、`<script>`、`onerror=`、`javascript:`、`../../etc/passwd`、`%2e%2e%2f` 必须拦下。两侧都钉，是因为只钉"能拦住"的规则很容易顺手把正常流量一起拦掉
- `WafEngineTests` 的另一半钉编译期语义：坏正则只丢那一条规则（其余照常）、既没检测器也没条件的规则**不能**匹配一切（否则一条 `action: block` 就把整条路由打死）、`tag` 没给值就丢弃、状态码越界只记 warning 并回落到 403、`requiresBody` 只在内置检测器或 body 条件存在时才为真、条件超时按不命中处理而内置检测器超时按命中处理
- `WafMiddlewareTests`（17 个）钉请求侧的实际行为：multipart 上传被转发、PDF 被转发、`--help` 被转发、JSON body 注入返回 403 且带 `X-Waf-Rule: sql-injection`、**检查过 body 之后下一个管线仍能读到完整 body**、超过扫描预算的 body 不被截断、monitor 模式只记录不拦、`allow` 规则能在同一路径上豁免、`tag` 规则让上游看到 `X-Waf-Tag`、自定义响应体与状态码生效、控制面路径永不被检查
- 另跑了一遍真实网关（SampleGateway + 本地 echo 上游，19 项断言全过）：multipart 上传 200 且**上游收到的字节数与发出的一致**（证明读完 body 复位没丢数据）、PDF 200、`?args=--verbose&cmd=--help` 200、`select … from` 文案 200、JSON body 注入 403 且 `X-Waf-Rule: sql-injection`、查询串注入 403、XSS 403、`..%2f..%2fetc%2fpasswd` 403、monitor 模式同一 payload 变 200、`allow` 规则只在指定前缀生效（同 payload 在前缀外仍 403）、tag 规则让上游看到 `partner`、自定义 429 + `slow down` 生效、全程 0 条未处理异常
- 用量 387 个（2.5.0 为 358）：新增 `GatewayEnvironmentRegistryTests`（13 个）、`SqliteEnvironmentConnectionStringTests`（11 个）、`EnvironmentIsolationIntegrationTests`（4 个，真实容器 + 真实 SQLite 文件）
- `SqliteEnvironmentConnectionStringTests` 钉的是几条会被想当然的地方：运行环境**原样返回**配置串（这是零迁移的根据）、后缀加在扩展名之前（`store.dev.sqlite` 而不是 `store.sqlite.dev`）、SQLCipher 的 `Password` 与其他参数在派生后仍然保留（丢了就只有切过去时才发现打不开）、`:memory:` 与不可解析串不做猜测
- `GatewayEnvironmentRegistryTests` 钉住 `Runtime` 永远在列表里（否则切走就切不回来）、大小写不敏感去重、以及 7 种可能逃出库目录的名字必须被拒绝
- `EnvironmentIsolationIntegrationTests` 是这批里最重要的一组：它不信任何单点，而是把容器、库、工作集、已发布快照串起来跑。其中 `Switching_to_a_non_runtime_environment_does_not_publish_or_forward_its_config` **在临时停用发布守卫后确实变红**（1 failed / 3 passed），确认它不是靠巧合绿着
- 另跑了一遍真实网关（SampleGateway + 两个真实 SQLite 文件，11 项断言全过）：`api/environments` 报 `runtime=prod multi=true`；prod 建 `prod-only` → 切 dev（`published=false`，列表为空）→ dev 建 `dev-only` → `prod.db` 与 `prod.dev.db` 两个文件同时存在 → 切回 prod（`published=true`）→ 列表回到 `prod-only`，`dev-only` 未出现；`../prod`、`..`、`pr od`、`prod.db`、`nope` 五种非法名全部 400；全程无未处理异常
- 面板的规则编辑器另外用无头 DOM 桩跑了 39 项**数据**断言（一次性验证，未加入测试项目 —— 视觉部分由人验收）。钉的是保存出来的 config 对不对：正常配置往返不丢字段；**monitor 不会被归一化改回 enforce**（实现时确实踩到过，`normalise()` 清默认值顺手把 `mode` 也删了）；编译器会静默丢弃的条件在面板里全部被拒绝（空值 / 非法正则 / 非法 CIDR / 缺 Header 名 / 空规则 / 状态码越界）；禁用的草稿规则不阻塞保存且 `enabled: false` 保留；旧绑定保存后不会凭空多出 `rules: []`（日志里会被读成"运维清空了规则"）；「转成可编辑规则」生成的三条与编译器合成顺序一致（路径穿越 → SQL 注入 → XSS）
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
- 158 个用例全绿（2.4.0 批次为 117），新增：批量校验 7 例、并发契约 15 例、守卫适配器 4 例、TTFR 8 例、连接池监控 7 例
- 新增 `tests/Aneiang.Yarp.UnitTests`（xUnit，net9.0，**117 个用例**，全绿）
  - `YarpJsonConfigTests`：空串/空白布尔、`"true"`/`"True"`、`""` 与 `00:00:30`/`30`/`30s`/`5m`/`2h`/`1d` 的 TimeSpan 解析、裸数字必须是秒而非天、空 JSON 不抛、非法 JSON 抛、SessionAffinity 往返
  - `ConfigurationValidatorTests`：路由/集群/全量体检共 40+ 断言点，含"同一 payload 内新建集群不误报"
  - `GatewayMetricsCollectorTests`：直方图累积性、`_sum` 单位为秒、负耗时钳制、标签转义（引号/反斜杠/换行）、基数上界与 overflow
  - `GatewayPolicyCatalogTests`：9 个内置策略全部可解析、大小写敏感、`Unverifiable` 语义、`AddGatewayPolicies` 与宿主 `AddCors` 共存
- 测试工程已加入 `Aneiang.Yarp.sln`

### 📊 交付进度

**v2.5 六项（全部交付于 2.3.0.40）**

| # | 项 | 状态 |
|:--:|:--|:--|
| 1 | 插件扩展点（LB 策略 / Transform） | ✅ `IGatewayPluginHostServices` + `AddPluginHostServices` |
| 2 | 权重灰度（`Weighted` 策略 + `X-Gateway-Canary` 钉住） | ✅ 精确分流 + 热更新 |
| 3 | 灰度应用 + 自动回滚（观察窗口 + 错误率阈值） | ✅ 状态机 + 归因判定 + 精确回滚 + Dashboard 面板 |
| 4 | 指标告警阈值（5xx 率 / P99 / 熔断开闸） | ✅ 三类规则 + 滑动窗口 + 持续判定 + 无数据保护 + 投递状态可见 |
| 5 | OpenTelemetry 追踪 | ✅ 分层 span + OTLP 导出 + 确定性采样 + 仅内存瀑布图 |
| 6 | 故障注入插件 | ✅ 延迟 / 错误码 / 连接中断 + 请求头门 + 一键关绑定 |

**v2.6 六项（2.3.0.40 全部交付）**

| # | 项 | 状态 |
|:--:|:--|:--|
| 1 | 多环境配置隔离与一键提升 | ✅ 1a 环境隔离与切换 + 1b 环境间 diff + 1c 两步提升（prepare/apply + 单次令牌 + 配置指纹复核） |
| 2 | RBAC（只读 / 运维 / 管理员） | ✅ 三角色 + 逐账号 2FA + 403 `reason` + 面板按角色隐藏写操作（ADR-004） |
| 3 | 官方 Docker 镜像 + Helm Chart + `dotnet new` 模板 | ✅ 三件套 + CI `deployables` 作业（模板真的编译、chart 真的渲染、镜像真的构建） |
| 4 | 插件市场（模板 + 版本兼容矩阵 + 契约测试） | ✅ `aneiang-yarp-plugin` 模板（`plugin.json` 由 MSBuild 渲染）+ 11 插件兼容矩阵 + 12 个契约测试 + 代理管线改按 `order` 排序 + **`<prefix>/plugin-market` 页面**（来源区分、按目录安装/升级/卸载、契约说明） |
| 5 | 管理 API OpenAPI 文档 | ✅ `EndpointDataSource` 生成 + OpenAPI 3.1（95 路径 / 110 操作）+ 零依赖渲染页 + 侧栏入口 |
| 6 | 自定义 WAF 规则（条件表达式 + 动作） | ✅ 十种来源 × 八种操作符 + 五种动作（含 `rate-limit`）+ monitor 预演 + 面板卡片式编辑器 |

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
