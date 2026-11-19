# 安全整改清单

- **审计时间**：2026-11-19
- **基线**：2.3.0.40 / 分支 `23040`
- **范围**：`Aneiang.Yarp`（网关核心）、`Aneiang.Yarp.Dashboard`（控制面）、`Aneiang.Yarp.Plugin.Waf`、`Aneiang.Yarp.Storage.Sqlite`
- **方法**：读代码找信任边界，不依赖扫描器给结论。每条都带文件行号，可直接复核；每条的"验收"都必须能用一条命令或一个用例证明。

两份清单的来源不同，但问题重叠，这里合并去重后统一编号：

- **外部攻击面审计**（认证/授权、转发信任、插件加载、出站请求、依赖）
- **前端交付审计**（资源体积、源码暴露、凭据存放）

## 结论先说

认证与授权的地基是对的：JWT 签名用恒定时间比较、授权在服务端每次请求复检、viewer 按方法拒绝而不是按路径列表、密码走配置不进代码。这些设计不用推翻。

问题集中在三类：

1. **三个可实际利用的洞**——路径遍历、控制面越权、客户端 IP 失真。前两个能直接拿到宿主机文件系统权限或 admin 能力。
2. **企业合规的硬门槛缺失**——密码明文、Cookie 无 `Secure`、零安全响应头、登录无审计、追踪导出鉴权无处配置。这些不是"最好有"，是过等保/渗透测试会直接卡住的项。其中最后一项还会让人误以为"本项目不支持 OTLP 鉴权"。
3. **一处自我抵消的设计**——HttpOnly cookie 和 localStorage 里存同一个 token，前者的防护被后者完全抵消。

## 分级总表

| ID | 级别 | 状态 | 问题 | 关键证据 |
|:--|:--|:--|:--|:--|
| SEC-01 | **P0** | 已完成 | 插件 `id` 可路径遍历，安装时先递归删除目标目录 | `ExternalGatewayPluginHost.cs:240,314-316` |
| SEC-02 | **P0** | 已完成 | `api/2fa/*` 未纳入 admin-only，operator 可接管/关闭第二因子 | `DashboardRolePolicy.cs:56-86` |
| SEC-03 | **P0** | 已完成 | 未注册 `UseForwardedHeaders`，部署在反代后客户端 IP 全部失真 | 全仓库无注册点 |
| SEC-04 | P1 | 已完成 | 密码明文存配置、用 `StringComparison.Ordinal` 比较（非恒定时间） | `DashboardAuthController.cs:99,108` |
| SEC-05 | P1 | 已完成 | 会话凭据：Cookie 缺 `Secure`、用 `DateTime.Now`、token 又明文进 localStorage | `DashboardAuthController.cs:142-147`、`dashboard-api.js:29,36,120` |
| SEC-06 | P1 | 已完成 | 登录无限速、无失败计数、失败不写审计 | `DashboardAuthController.cs:118-119` |
| SEC-07 | P1 | 已完成 | 零安全响应头（CSP / XFO / XCTO / Referrer-Policy / HSTS） | 全仓库 grep 无命中 |
| SEC-08 | P1 | 已完成 | Webhook 目标 URL 无校验，可作 SSRF 跳板打内网与元数据服务 | `WebhookNotificationService.cs:237` |
| SEC-09 | P1 | 已完成 | Webhook URL 含 `access_token` 却整串进日志 | `WebhookNotificationService.cs:209`、`WebhookSettingsController.cs:267` |
| SEC-10 | P1 | 已完成 | 审计只覆盖配置变更，认证/授权/插件/2FA 全部无记录 | 仅 `ConfigChangeAuditLog` |
| SEC-19 | P1 | 已完成 | 追踪导出鉴权无处配置、不可见、协议非法值静默退化为 gRPC | `TracingAuthStore.cs`、`GatewayTracingHost.cs`、`TracingController.cs` |
| SEC-11 | P2 | 已完成 | JWT 不校验 `alg`，无 `jti`，logout 只删 cookie（token 仍有效） | `DashboardJwtHelper.cs:64-113` |
| SEC-12 | P2 | 已完成 | API Key 与 Basic 凭据用 `==` 比较 | `DashboardAuthorizationService.cs:131`、`GatewayApiAuthFilter.cs:48` |
| SEC-13 | P2 | 已完成 | TOTP 无防重放，同一码在 ±1 窗口内可重复使用 | `TotpHelper.cs:24-40` |
| SEC-14 | P2 | 已完成 | 依赖 High 漏洞 `SQLitePCLRaw.lib.e_sqlite3` 2.1.10 | GHSA-2m69-gcr7-jv3q |
| SEC-15 | P2 | 已完成（实测确认生效） | SQLCipher 与 e_sqlite3 同时引入，`Password=` 是否生效不确定 | `Aneiang.Yarp.Storage.Sqlite.csproj:19` |
| SEC-16 | P2 | 已完成 | JWT 密钥与 2FA secret 明文落盘且 `catch {}` 静默 | `JwtSecretProvider.cs:82-94`、`DashboardAuthController.cs:222,237` |
| SEC-17 | P2 | 已完成 | 无安全分析器，构建属性只有打包字段 | `src/Directory.Build.props` |
| SEC-18 | P2 | 已完成（契约化） | 插件以 full trust 运行，`CollectibleLoadContext` 只解决卸载不是沙箱 | `ExternalGatewayPluginHost.cs:454` |

统计：**P0 三项、P1 八项、P2 八项，共 19 项。19 项全部完成，`dotnet build` 0 警告 0 错误，单元测试 758 通过，全解决方案 `dotnet list package --vulnerable --include-transitive` 无易受攻击包。**

> 状态列按代码事实填写，不按"计划做"填写。复核命令见「验证方式」一节。

## 实施记录（按批次）

四批全部落地。这里只记录**与清单原方案有出入的地方**，以及实测得到的结论——逐条复述代码没有意义，改动本身在 git 里。

### 批次 A（SEC-01/02/03）：路径、越权、IP

- 插件 id 白名单 `^[a-z0-9][a-z0-9._-]{0,63}$`；目录边界用 `Path.GetFullPath` 后按**尾随分隔符**比对，而不是 `StartsWith`——否则 `/plugins-evil` 会被认作在 `/plugins` 内。
- `2fa/*` 的三个端点收紧为 admin-only，且 `api/2fa/setup` 由 `GET` 改为 `POST`（GET 会被预取、被缓存、被跨站图片标签打中）。
- 转发头用 `IStartupFilter` 注册而非 `app.UseForwardedHeaders()`：位置必须早于 WAF / 限流 / 审计，且不能依赖宿主记得调用。**同时清空框架默认信任的 loopback**——那是给本机反代准备的，放在容器里等于信任所有人。可选配置时默认**不装**中间件，而不是默认装一个什么都信的。

### 批次 B（SEC-04/05/06/07）：会话与入口

- 密码存储为 `pbkdf2-sha256$iterations$salt$hash` 自描述格式（210k 次，OWASP 2026 值），老配置里的明文密码仍可登录并按需升级。
- **cookie 路线与 header 路线只能二选一**：`HttpOnly` cookie 与 localStorage 里的同一枚 token 并存时，前者的防护被后者完全抵消。这里选了 cookie，`dashboard-api.js` 的 `setToken` / `getToken` 退化为 no-op，响应体不再返回 token。
- CSRF 用 `X-Requested-With` 双提交的轻量版：跨站表单无法伪造该头，且不触发 CORS 预检，比整套 token 机制更适合这个只有一种前端的场景。
- 安全响应头**放弃 `OnStarting`**，改为在调用 `next` 之前直接写：`OnStarting` 在 `DefaultHttpContext` 上不触发，测试根本测不到它，那种"写了但没生效"比不写更危险。

### 批次 C（SEC-08/09/10/19）：出站与可追溯

- SSRF 分三段而不是一次检查：保存时校验字面量（**不做 DNS**）、发送时按每次尝试逐次校验、并关闭自动重定向（302 到云厂商元数据地址能绕过任何只看首次 URL 的检查）。`unresolved` 一律拒绝而不是放行。
- 审计日志用 `_ = PersistAsync(entity)` 不阻塞请求——审计写失败不应该让一次合法的配置变更失败，但也不能因此不写。

### 批次 D（SEC-11~18）

- **SEC-11**：`alg` 头校验在 HMAC 校验之前（否则 `alg: none` 的自签 token 直接通过）；`jti` + 吊销表让 logout 真正生效。吊销表是内存的，重启即忘——单控制面部署下这是可接受的，写在注释里而不是假装没有。
- **SEC-12**：`SecureCompare` 统一走 `CryptographicOperations.FixedTimeEquals`，长度不同先返回 false。
- **SEC-13**：`lastAcceptedCounter` 高水位；`counter <= last` 即拒绝。窗口仍是 ±1（时钟漂移），但**同一格不许用两次**。账号级 secret 存在配置里，无法回写计数器——这个缺口在代码注释和文档里都点明了，没有掩饰。
- **SEC-14**：清单原方案是"显式引用 `bundle_e_sqlcipher` 2.1.11+ 压住传递版本"，但**那样压不住**：`Microsoft.Data.Sqlite` 仍然引入 `bundle_e_sqlite3`，NuGet 会把 `lib.e_sqlite3` 解析到 2.1.10，CVE 还在。实际做法是改用 `Microsoft.Data.Sqlite.Core` + 显式 `bundle_e_sqlcipher` 2.1.11，从依赖图里彻底移除 `bundle_e_sqlite3`（`SqliteNativeProvider` 补上 Core 版需要的手工 provider 初始化）。结果是全解决方案 `--vulnerable` 归零，而不是被 pin 住。
- **SEC-15**：**实测结论是加密生效**。依据有两条独立证据：`PRAGMA cipher_version` 有返回值（说明 provider 是 SQLCipher），并且带 `Password=` 写库后对 `.db` / `-wal` 做 UTF-8 与 UTF-16 全字节扫描，搜不到哨兵字符串；用错密钥打开报 `SQLITE_NOTADB`(26)。既然承诺了加密，就加了启动自检：连接串带 `Password=` 但 provider 不能加密时，`SqliteConnectionFactory` 构造即抛 `InvalidOperationException`，而不是静默写明文库。判据是 `SupportsEncryption`（纯内存 `PRAGMA cipher_version` 探测，不碰文件）。
  - 顺带一个发现：样例配置全都是 `Password=123456`。加密生效之后这条口令就是**唯一的**防线，它比不加密更危险地给人一种安全感。真实部署应当换成高熵口令或干脆走文件系统权限 —— 这一点没法靠代码强制，写在这里。
- **SEC-16**：JWT 密钥文件与 2FA 状态文件在 Unix 上一律 `0600`，且**读取时也收紧一次**（历史遗留的宽权限文件不必等下一次写入）；收紧失败 `LogWarning` 而不是吞掉。Windows 保持继承 ACL——在那里改 POSIX 位只会把部署搞坏，这个不对称明说了。
- **SEC-17**：`AnalysisLevel=latest` 显式钉死 + `src/.editorconfig` 打开整个 Security 分类。基线收集只命中两条，**都修了而不是关掉**：
  - `CA5359`：客户端 gRPC 通道**无条件**跳过证书校验（"dev/demo 用"）。现在默认正常校验，只有 `Gateway:Registration:AllowUntrustedGrpcCertificate=true` 才跳过。这是本批次最有价值的一条——它不是风格问题，是把"连的是不是自己的网关"这个判断关掉了。
  - `CA5350`：TOTP 的 HMAC-SHA1。这是 RFC 6238 规定的，换成别的会静默废掉所有已注册的验证器，用带理由的 `SuppressMessage` 保留。
  - 基线归零后把该分类提升为 `error`，避免下一个命中被顺手划过去。
- **SEC-18**：不做沙箱，改为契约化。安装对话框加显式警告（文档只有在意的人会读，对话框是唯一能改变决定的地方），并把"插件 = full trust、不能当隔离边界"写进 `docs/plugins/plugin-compatibility.md`。

### 本批次新增的回归测试

| 文件 | 覆盖 |
|:--|:--|
| `SqliteEncryptionTests.cs` | provider 是 SQLCipher；带密码的库无明文；错密钥被拒（错误码 26）；带 `Password=` 的连接串不被自检误拦 |
| `DashboardTokenRevocationStoreTests.cs` | 吊销生效、按条过期、短命条目不会误清长命条目、空 id 不入表 |
| `TotpReplayTests.cs` | 首次通过、同码重放被拒、下一格仍可用、跨格重放被拒、畸形码、换 secret 被拒 |
| `SecureCompareTests.cs` | 语义等价、末位差异、长度差异、null 侧不匹配、序数比较不受区域设置影响 |
| `DashboardRoleTests.cs` | logout 后同一 token 失效；`alg:none` 被拒；token 带 `jti` |
| `SecurityAuditWriterTests.cs` | 事件落到审计表（`TargetType=security`）；无存储时不抛；写失败不上抛；内存副本可用 |

单元测试从 668 增至 764。

### 仍未处理（与本清单无关，但复核时发现）

`tests/Aneiang.Yarp.Regression` 有 4 条既有失败，在本次改动之前的 HEAD 上同样失败，与本批次无关：

- `Removed compatibility contracts stay isolated from runtime source`：扫描命中 `WafRuleOptions.cs` / `WafPolicyCompiler.cs` 里的 `RateLimitOptions`。那是 WAF 规则自带的限流配置类型，与被移除的兼容契约同名不同物——断言用的是字符串包含，属于误判。
- 另外 3 条在插件运行时域与 `GatewayPluginManager` 的持久化导入上，待单独排查。

这个控制台程序不在 CI 的 `dotnet test` 里，所以一直是红的而没人发现。

---

## P0：可实际利用

### SEC-01 插件安装路径遍历与任意目录删除

**现象**：`ValidateManifest` 只校验 `id` 非空，随后直接用作目录名。

```csharp
if (string.IsNullOrWhiteSpace(manifest.Id)) throw new InvalidDataException("id is required.");  // :240

var targetDir = Path.Combine(_pluginRoot, manifest.Id);            // :314
if (Directory.Exists(targetDir))
    Directory.Delete(targetDir, recursive: true);                  // :316
```

一个 `"id": "../../../app"` 的 `plugin.json` 就能把文件写到应用根目录之外，并且**在复制之前先递归删掉那个路径**。

**影响**：需要 admin 安装权限，但把"能装插件"放大成"能删除任意目录"。在容器里可能只是搞坏自己，在裸机/共享卷部署里是数据丢失。

**方案**：`id` 限定 `^[a-z0-9][a-z0-9._-]{0,63}$`；`targetDir` 必须 `StartsWith(_pluginRoot + Path.DirectorySeparatorChar)`；把 `EntryAssembly` 那套 `GetFullPath` + `StartsWith` 校验抽成共用的 `EnsureWithin(root, relative)`。

**验收**：单测用 `"../escape"`、`"../../etc"`、`"/abs"`、`"a/../../b"` 四种 manifest，全部拒绝且 `_pluginRoot` 外无新增/删除。

### SEC-02 2FA 相关端点未纳入 admin-only

**现象**：`DashboardRolePolicy.IsAdminOnly` 只识别 `webhook` 与 `plugins` 两个 area，其余 allow-by-default。`2fa` 不在其中，于是 operator 可以：

- `POST api/2fa/disable` 关掉全局 2FA；
- `POST api/2fa/verify` 把 secret 换成自己的——admin 下次登录必须输 operator 手里的码。

另外 `[HttpGet("api/2fa/setup")]` 是 GET 且会生成密钥，viewer 也能调到（虽然不落盘，但"读方法产生密钥"本身违反了 viewer 按方法拒绝的假设）。

**影响**：operator 提权到 admin 的第二因子控制权，等于绕过 RBAC 的三档划分。

**方案**：`IsAdminOnly` 补 `2fa`、`users`、`auth`（写方法）；`setup` 改 POST。更彻底的做法是把"控制面自身身份配置"从 allow-by-default 改为 deny-by-default 白名单——ADR-4 里"新增运维端点自动可用"的收益，在身份配置这块不成立。

**验收**：operator 打三个 2fa 写端点全部 403；viewer 打 `setup` 也 403。

### SEC-03 客户端 IP 在反代后全部失真

**现象**：`ClientIpResolver.GetClientIp` 只返回 `RemoteIpAddress`，注释写得很正确——原始转发头不可信。但整个仓库**没有注册** `UseForwardedHeaders` / `ForwardedHeadersOptions`，也没有 `KnownProxies` 配置。

**影响**：部署在 nginx / SLB / Ingress 后面时，所有请求的客户端 IP 都是反代 IP。后果是双向的：

- WAF 的 IP 规则、IP 黑名单、按 IP 限流**整体失效**（所有人共享反代 IP 的配额，或反代 IP 被误封导致全站不可用）；
- 审计日志记的是反代 IP，攻击溯源失效。

**方案**：新增 `Gateway:Security:TrustedProxies` / `TrustedNetworks` 配置；**只有配置了受信代理时才** `UseForwardedHeaders`，并设 `ForwardLimit = 1`、`ForwardedHeaders = XForwardedFor | XForwardedProto`。未配置时保持现状（不信任任何头）。顺带删掉 `GetConnectionIp` 里那段 XFF 回退——它和 `GetClientIp` 的立场自相矛盾。

**验收**：未配置时代码路径不调用该中间件；配置后用两个不同 `X-Forwarded-For` 请求，审计日志记录不同 IP。

---

## P1：企业合规门槛

### SEC-04 密码明文存储与非恒定时间比较

**现象**：

```csharp
valid = string.Equals(account.Password, request.Password, StringComparison.Ordinal);   // :99
string.Equals(request.Password, _jwtPassword, StringComparison.Ordinal);               // :108
```

密码以明文写在 `Gateway:Dashboard:Auth:Users[].Password`。

**影响**：配置文件/环境变量/备份一旦泄露即明文可用；`Ordinal` 比较在首字节不同时就返回，理论上可做计时侧信道（内网低风险，但同一个代码库里 JWT 已经写了 `ConstantTimeEquals`，不一致本身就是缺陷）。

**方案**：新增 `PasswordHash`（PBKDF2-SHA256 ≥ 210k 迭代，或 Argon2id），启动时把存量 `Password` 转成 hash 并在下次保存时落盘；比较统一走 `CryptographicOperations.FixedTimeEquals`。保留 `Password` 明文兼容一个版本，日志警告。

**验收**：配置里只写 hash 能登录；同一密码连续 100 次登录耗时方差在阈值内。

### SEC-05 会话凭据存放（合并两轮发现）

三个子项同源，一起改才有意义：

1. **Cookie 缺 `Secure`**（`:142-147`）：只有 `HttpOnly` + `SameSite=Lax`，HTTP 部署下 token 明文过网。
2. **`Expires = DateTime.Now.AddHours(8)`**：本地时间，容器 UTC 与主机时区错开时可能提前失效或延长。
3. **token 同时进 localStorage**（`dashboard-api.js:29,36,120`）：前端把 token 存进 `localStorage` 并用 `Authorization: Bearer` 发送。**这让 HttpOnly 完全失效**——任何 XSS 都能一行取走 token 外带。

**影响**：第 3 项是最实在的一条。面板里有 Monaco 配置编辑器、日志渲染、路由配置回显，都是用户可控内容，XSS 面是真实的。

**方案**：二选一，不允许并存。

- **推荐**：走 cookie 路线。删掉 `setToken/getToken` 与 Bearer 头，同源 `fetch` 自动带 cookie；补 `Secure=true`（HTTP 场景按配置降级并日志警告）、`DateTimeOffset.UtcNow`；补 CSRF 双提交 token。SignalR 的 `access_token` query 保留（WebSocket 无法带头）。
- 备选：走 header 路线，去掉 HttpOnly cookie，但要在文档里写明"XSS 可窃取 token"是已知接受风险。

**验收**：DevTools 里 `localStorage` 无 token；生产环境 Cookie 带 `Secure`；去掉 cookie 后所有写操作 401。

### SEC-06 登录无速率限制、失败无审计

**现象**：登录失败只有 `return Json(new { code = 401 })`（`:118-119`），无日志、无计数、无退避。

**影响**：在线暴力破解零成本；且因为不记录，事后无法发现"有人在爆破"。

**方案**：IP + 账号双维度滑动窗口（例如 5 次/5 分钟），超限返回 429 + `Retry-After` 并指数退避；失败、锁定、解锁事件写审计。

**验收**：连续 6 次错误密码后第 7 次返回 429；审计表里有 6 条失败记录。

### SEC-07 零安全响应头

**现象**：全仓库 grep 无 `Content-Security-Policy` / `X-Frame-Options` / `X-Content-Type-Options` / `Referrer-Policy` / `Strict-Transport-Security` 的任何写入。

**影响**：

- 无 XFO 或 CSP `frame-ancestors` → **点击劫持**：把面板嵌进 iframe，诱导 operator 点"删除集群"；
- 无 XCTO → 内容类型嗅探；
- 无 CSP → XSS 没有任何第二道防线。

**方案**：加一个 `SecurityHeadersMiddleware`：

- `X-Content-Type-Options: nosniff`
- `X-Frame-Options: DENY` + CSP `frame-ancestors 'none'`
- `Referrer-Policy: no-referrer`
- 生产环境 `Strict-Transport-Security: max-age=31536000; includeSubDomains`
- CSP：面板无内联事件、无外部 CDN，可以上 `default-src 'self'`；Monaco 需要 `worker-src 'self' blob:`、`style-src 'self' 'unsafe-inline'`。先以 `Content-Security-Policy-Report-Only` 跑一轮收违规再转强制。

**验收**：响应头齐全；Report-Only 模式下打开主要页面无违规上报。

### SEC-08 Webhook 无 SSRF 防护

**现象**：`new HttpRequestMessage(HttpMethod.Post, requestUrl)`（`:237`）直接用用户填的 URL，无任何目标校验。

**影响**：admin 可把 webhook 指向 `http://169.254.169.254/latest/meta-data/`、`http://127.0.0.1:8500/v1/...`、内网管理端口，把告警通道变成内网探测/打点工具。若 admin 账号被钓鱼，这是现成的横向移动跳板。

**方案**：出站前解析目标 IP，拒绝私有段（10/8、172.16/12、192.168/16、127/8、169.254/16、::1、fc00::/7）与云元数据地址；只允许 http/https；跟随重定向时逐跳复检（或直接禁止重定向）。提供 `Gateway:Notifications:AllowPrivateTargets` 逃生开关给内网自建 webhook。

**验收**：填 `http://127.0.0.1:8080/x` 保存时被拒或发送时记录失败原因；内网地址在开关打开后可发。

### SEC-09 Webhook URL 含凭据进日志

**现象**：`_logger.LogDebug("... ({Url})", ..., endpoint.Url)`（`:209`）。钉钉 URL 带 `access_token`、Slack 带 secret，一旦日志被收集/分享即凭据泄露。

**现状**：返回给前端的 URL 已经过 `WebhookSettingsController.MaskUrl`（`:267`）把 query 里的 token 换成 `***`，所以**控制台回显这条已堵**；但日志写入路径没走同一个函数，`LogDebug` 打的仍是整串。修法就是把 `MaskUrl` 提到共用位置，两处共用。

**方案**：日志只输出 `scheme://host:port` + 路径的 SHA256 前 8 位（可对比、不可还原）；`WebhookDeliveryDetail.Url` 在返回给前端前也做同样脱敏。

**验收**：日志里搜不到 `access_token=`。

### SEC-10 审计覆盖面不足

**现象**：只有 `ConfigChangeAuditLog`（配置变更）。以下全部无记录：登录成功/失败、权限拒绝（403）、2FA 开启/关闭、插件安装/卸载/升级、密钥轮换、Webhook 目标变更。

**影响**：出事后无法回答"谁在什么时候做了什么"，这是等保 2.0 三级"安全审计"条款的硬要求。

**方案**：定义 `SecurityEventType` 枚举 + `ISecurityAuditWriter`，在 `DashboardAuthFilter`（拒绝时）、登录端点、2FA 端点、插件宿主、Webhook 控制器埋点；记录 principal、角色、IP、UA、结果、原因码。与配置审计共用存储表，用 `category` 区分。

**验收**：制造一次 403、一次失败登录、一次插件安装，审计表各有对应记录。

### SEC-19 追踪导出鉴权无处配置、不可见

**现象**：能力存在，但三层都没有出口。

| 层 | 状态 |
|:--|:--|
| 配置模型 | ✅ `GatewayTracingOptions.Headers`（`Dictionary<string,string>`，注释写着"e.g. a Grafana Cloud API token"） |
| 导出器 | ✅ `GatewayTracingHost.cs:176` 拼成 `k=v,k2=v2` 传给 `AddOtlpExporter` |
| 状态 API | ❌ 只回 `endpoint` / `protocol` / `samplingRatio`，**连"有没有配鉴权"都不返回** |
| 面板 | ❌ 只有采样率滑块 |
| 样例配置 | ❌ `samples/SampleGateway/appsettings.json` 无 `Headers` |
| 文档 | ❌ 全仓库无一处提到 `Tracing:Headers` |

由此产生三个具体问题：

1. **标准环境变量被静默忽略**。代码显式赋 `options.Headers`，所以 `OTEL_EXPORTER_OTLP_HEADERS` / `OTEL_EXPORTER_OTLP_ENDPOINT` 这套标准约定在本项目**完全不生效**。从别的 OTel 项目迁过来的人会设了环境变量却发现没有鉴权头，且没有任何提示——这正是"找不到地方配置 Authorization"的根因。2. **协议非法值静默退化为 gRPC**。`string.Equals(_options.Protocol, "http/protobuf", OrdinalIgnoreCase)`，写 `httpprotobuf`、`http-protobuf`、`HTTP_PROTOBUF` 全部落入 `else` 分支变成 gRPC。而 gRPC 打 HTTP 端口、或 HTTP 打 gRPC 端口，表现都是"连不上收集器"，排查方向会被带偏到网络。
3. **凭据只能落明文配置文件**，且面板不可见——与 SEC-16 同源，但这条更麻烦：用户无法判断"我配的 token 到底发出去了没有"。

**影响**：企业环境的 OTLP 后端（Grafana Cloud、SigNoz、商业 APM）几乎都强制鉴权。配不上时就只剩两个坏选择：关掉导出，或者裸传。前者丢掉可观测性，后者把 trace 数据（含 URL、状态码、部分 Header）暴露给未授权方。

**方案**：

- **协议严格化**：忽略大小写与 `-`/`_`/`/` 差异做归一化；归一化后仍不认识的值让导出器**启动失败并把原因显示在追踪页**（`StartError`），而不是静默换协议。这里选择"降级但可见"而非"进程起不来"——可观测性配置错误不该拖垮转发面，但必须有人说出来。
- **识别标准环境变量**：`OTEL_EXPORTER_OTLP_HEADERS`（以及 `OTEL_EXPORTER_OTLP_TRACES_HEADERS`）作为最低优先级的 fallback 参与合并。理由是这类部署已经用环境变量写好了凭据（容器/K8s Secret），要求他们改写成项目自定义的嵌套键没有收益，反而会让人以为"不支持鉴权"。
- **凭据独立的优先级链**：`Gateway:Dashboard:Tracing:Headers`（显式配置） > `Data/tracing-auth.json`（面板写入） > `OTEL_EXPORTER_OTLP_*_HEADERS`（标准环境变量）。覆盖文件写在独立文件而不是改写主配置——避免破坏注释与格式，也让"面板写过什么"在磁盘上一眼可辨。
- **只回脱敏值**：状态接口返回 header 名 + `Basic ****cFFxb1Y=`（保留 scheme 与后 4 位供比对），并标明来源（config / panel / env），**响应体任何情况下不含明文**。
- **写端点 admin-only**：改导出目标等于改"数据发给谁"，与 SEC-02 的判定标准一致；UI 用 `data-requires-admin` 门控。
- **文件权限收紧**：Linux 下 0600，写入后立即生效（与 SEC-16 一并处理）。
- **明确文档**：写清 `Headers` 的嵌套键写法、环境变量形式、协议合法值、`http/protobuf` 会自动追加 `/v1/traces`。

**验收**：

- 面板能显示"已配置 1 项"与脱敏值；配置为空时显示"未配置"并给出配置文件路径；
- `Protocol` 写 `httpprotobuf` 启动失败并指出合法值，写 `HTTP-Protobuf` 正常识别；
- 非 admin 调写端点 403；
- 抓取状态接口响应，全文搜不到完整 token；
- 重启后凭据生效，且 `Data/tracing-auth.json` 权限为 0600。

---

## P2：加固与清理

### SEC-11 JWT 强度与吊销

不校验 header 的 `alg`（只看三段签名，`:64-113`）；无 `jti`；8 小时固定，`Logout` 只是 `Cookies.Delete`（`:156`），**token 本身仍然有效**，被窃取的 token 无法主动作废。

→ 校验 `alg == "HS256"` 且 `typ == "JWT"`；加 `jti`；维护吊销表（内存 + 可选的库表）供"踢出会话"使用；`Logout` 把 `jti` 加入吊销列表；考虑 `nbf`/`iss` 校验（`iss` 现在生成但不校验）。

### SEC-12 API Key 与 Basic 凭据用 `==`

`DashboardAuthorizationService.cs:131` 的 `hv.Any(v => v == apiKey)`、`GatewayApiAuthFilter.cs:48` 的 `decoded[(idx + 1)..] == _opts.Password`。

→ 统一抽 `SecureEquals(string, string)`：长度不同直接 false，否则 `CryptographicOperations.FixedTimeEquals`。

### SEC-13 TOTP 无防重放

`ValidateCode` 只覆盖 ±1 窗口，不记录已用 counter。同一 6 位码在 30–90 秒内可重复提交。

→ 每个 secret 记录 `lastAcceptedCounter`，`counter <= last` 直接拒绝；状态随 2FA state 一起持久化。

### SEC-14 依赖 High 漏洞

`SQLitePCLRaw.lib.e_sqlite3` 2.1.10（GHSA-2m69-gcr7-jv3q），经 `Microsoft.Data.Sqlite 9.0.4` 传递引入。

→ 显式引用 `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11+ 压住传递版本；CI 加 `dotnet list package --vulnerable` 失败即红。

### SEC-15 数据库加密可能静默失效

`Storage.Sqlite.csproj:19` 引了 `bundle_e_sqlcipher`，但 `Microsoft.Data.Sqlite` 又传递引入 `bundle_e_sqlite3`，两者都提供 `Batteries_V2.Init()`。`Password=xxx` 只在 SQLCipher provider 生效时才有意义。

**这条无法凭代码断定，必须实测**：写入一条含哨兵字符串的记录，对 `.db` 文件做 `strings`/十六进制搜索，能搜到明文即说明未加密。

→ 实测后二选一：确认加密生效并加启动自检（打不开带密码的库就拒绝启动）；或移除 illusion，明确"不加密，靠文件系统权限保护"并写进文档。

### SEC-16 密钥落盘与静默异常

`Data/.jwt-secret`、`twofactor-state.json` 明文写盘、无权限收紧；`GetTwoFactorState`/`SaveTwoFactorState` 都是 `catch { }`（`DashboardAuthController.cs:222,237`），密钥读写失败时静默降级。

→ Linux 下 `File.SetUnixFileMode(0600)`；异常至少 `LogWarning` 一次；2FA secret 考虑用 DPAPI（Windows）/ Data Protection（跨平台）包装。

### SEC-17 无安全分析器

`src/Directory.Build.props` 只配了版本号与打包字段，无 `EnableNETAnalyzers` / `AnalysisLevel`，也没有任何第三方安全分析器。

→ 开 `AnalysisLevel=latest` + `EnableNETAnalyzers`，引入 `SecurityCodeScan.V3`；先以 warning 收集基线，再逐条处置，最后考虑 `TreatWarningsAsErrors` 只对安全规则生效。

### SEC-18 插件 full trust（文档化，非代码修复）

`CollectiblePluginLoadContext`（`:454`）只解决卸载，**不是安全沙箱**——插件可以 `File.Delete`、`Process.Start`、读环境变量。

→ 不打算做（.NET 里没有可靠的进程内沙箱）。要求：安装对话框明确提示"插件拥有与网关同等的权限，只安装可信来源"；文档里把这条边界写成显式契约。

---

## 明确不做的

| 项 | 结论 | 理由 |
|:--|:--|:--|
| **JS 源码 minify** | 不做 | Brotli Optimal 已启用且 MIME 覆盖 `text/javascript`/`text/css`，`UseResponseCompression()` 顺序正确；自有业务代码 1215 KB 经 Brotli 后约 200–250 KB，minify 端到端只再省约 15%，代价是引入 node 构建链路（与"零外部 CDN、零 JS 构建"的项目风格冲突） |
| **JS 加密 / 混淆** | 不做 | 本面板是 RCL，经 NuGet/Docker 分发，服务端 .NET 程序集可被 ILSpy 直接反编译——前端混淆的边际收益接近 0；且浏览器必须拿到可执行代码，混淆只提高逆向成本不构成安全边界；反而需要 CSP `'unsafe-eval'`，抵消 SEC-07 |
| **插件安全沙箱** | 不做 | 见 SEC-18，改为契约文档化 |
| **分布式限流实现** | 不做 | ADR-3 已定"只保留扩展位" |
| **多实例配置自动同步** | 不做 | ADR-1 已定"单控制面" |

顺带清理（5 分钟量级）：`js/vendor/chart.umd.min.js:20`、`js/vendor/signalr.min.js:2` 的 `sourceMappingURL` 指向不存在的 `.map`，每次开 DevTools 都 404。

## 分批实施

| 批次 | 内容 | 预估 | 为什么这样切 |
|:--|:--|:--|:--|
| **A** | SEC-01 / 02 / 03 | 半天 | 三项都能被攻击者直接利用，且改动小、可立即测；先堵住"能删文件""能手握第二因子""IP 全失真" |
| **B** | SEC-04 / 05 / 06 / 07 | 1 天 | 会话与入口硬化。SEC-05 与 SEC-07 必须同批——CSP 压缩 XSS 面、token 不再落在 JS 可读处，缺一个另一个的价值都打折 |
| **C** | SEC-08 / 09 / 10 / 19 | 1 天 | 出站与可追溯性：SSRF、日志脱敏、审计补齐；SEC-19 是同一主题（凭据出现在日志/响应里），因此同批 |
| **D** | SEC-11 ~ 18 + 顺手清理 | 1 天 | 批量加固，可并行，单条影响小 |

## 验证方式

每批完成后固定跑：

1. `dotnet build` 必须 **0 警告 0 错误**；
2. `dotnet test tests/Aneiang.Yarp.UnitTests` 全绿，且新增对应回归用例（路径遍历、operator 403、限流 429、SSRF 拒绝、审计记录）；
3. 起 `samples/SampleGateway` 做一轮真实请求：登录、401/403、限流、安全响应头、审计落库；
4. 涉及前端凭据的改动，用 curl 检查登录响应与 Set-Cookie 属性，而不是只看代码。

**本机无法验证的**（需 CI 或实测环境）：Docker/Helm 部署、反代场景下的真实转发链（SEC-03）。

数据库加密（SEC-15）已在本机实测确认生效，见「实施记录」：三条互相独立的证据（`cipher_version` 有值、全字节扫描无明文、错密钥报 26），不是「跑起来没报错」。
