# 部署

三种拿走这套网关的方式，按"你要不要自己拥有这个镜像"区分：

| 方式 | 适合 | 命令 |
|:--|:--|:--|
| **`dotnet new` 模板** | 你要自己的代码仓库、自己的路由和插件组合 | `dotnet new aneiang-yarp -n MyGateway` |
| **Helm Chart** | 你已经有 K8s 集群，想快点跑起来看效果 | `helm install gw deploy/helm/aneiang-yarp` |
| **官方镜像** | 本地试跑、CI 里的集成测试、docker compose | `docker build -f deploy/docker/Dockerfile -t aneiang/yarp-gateway .` |

官方镜像里跑的是本仓库的 `SampleGateway`。**它不是给你 `FROM` 继承的基底镜像** —— 网关是一个引用了
Aneiang.Yarp 包的项目，你的路由、你的插件、你的账号都在你自己的仓库里。继承别人的二进制不算拥有它。

---

## 三种方式共用的事实

### 只有一个副本是对的

配置存储是一个 SQLite 文件。两个副本 = 两份互不知道对方存在的路由表，负载均衡器把面板请求轮流发过去，
于是"我刚保存的路由不见了"。这正是 ADR-001 要消除的那类静默不一致。

要扩数据面，用 `Deployment:Mode: Split`，或者让多个网关进程对同一个控制面，**不要** `replicaCount: 2`。
Helm chart 会直接拒绝渲染这种配置（`allowMultipleReplicas` 是给"用 ReadWriteMany 共享存储并且接受
last-write-wins"的人留的逃生口）。

### 配置存储必须挂在容器外

不挂卷的时候数据库在容器里，重启即失，而它装的是全部路由、快照和审计记录。两个官方产物都已经处理了：
Dockerfile 里 `VOLUME /app/data` + 绝对路径连接串，Helm chart 里 PVC 带 `helm.sh/resource-policy: keep`
（`helm uninstall` 不该顺手删掉你所有路由）。

### 认证默认是开的，密码是随机的

- Helm：`auth.jwtPassword` 留空时安装过程生成一个随机密码，写在 `NOTES.txt` 里，并从 Secret 里可以随时取回。
  比在公开的 chart 里写一个已知密码、然后指望有人去改要诚实。
- 模板/镜像：`appsettings.json` 里是 `change-me-before-you-expose-this`。**在它可被访问之前改掉。**
- `JwtSecret` 不设的话每个进程自己生成一个 —— 重启会让所有人掉线，多实例之间也互相不认。
  生产环境显式设置。

### 探针用的是业务端口

面板和代理共用同一个端口（面板在 `RoutePrefix` 下），所以没有单独的管理端口可以探。
`/health` 给 liveness、`/ready` 给 readiness，都在 5200 上。

---

## Docker

```bash
# 构建上下文是仓库根目录：镜像要从源码构建，而不是从 deploy/docker 里 COPY 一个 bin/
docker build -f deploy/docker/Dockerfile -t aneiang/yarp-gateway:local .

docker run --rm -p 5200:5200 -v gw-data:/app/data aneiang/yarp-gateway:local
```

镜像里已经处理的几件事：非 root（uid 10001）、`HEALTHCHECK` 打 `/health`、连接串指到 `/app/data`、
`curl` 只为探针而装（aspnet 基础镜像里既没有 curl 也没有 wget，探针命令不存在时的"永远不健康"
比没有探针更糟）。

`.dockerignore` 在**仓库根目录**，因为它只对构建上下文根生效 —— 放在 `deploy/docker/` 里会被忽略。

## Helm

```bash
helm install gw deploy/helm/aneiang-yarp \
  --namespace gateway --create-namespace \
  --set auth.jwtPassword='一个真正的密码' \
  --set auth.jwtSecret="$(openssl rand -base64 48)"
```

常用覆盖：

```bash
# 用你自己的镜像
--set image.repository=registry.example.com/gateway --set image.tag=2026.11.18

# 改用已有的 PVC（比如从别的 release 迁过来）
--set persistence.existingClaim=gw-data

# 关掉 gRPC 端口（没有客户端自注册）
--set grpc.enabled=false

# 挂 ingress
--set ingress.enabled=true --set ingress.className=nginx --set ingress.hosts[0].host=gw.example.com
```

chart 里几个不那么显然的选择，都写在对应模板的注释里：

- **`strategy: Recreate`**，不是 RollingUpdate。ReadWriteOnce 的卷不能让新旧两个 pod 同时挂，滚动更新会卡在
  Multi-Attach 错误上。
- **gRPC 单独一个 Service**，不是同 Service 多端口。很多 ingress controller 是按 Service 决定后端协议的，
  把 h2c 和 HTTP/1.1 放在一起，要么被忽略，要么把整条链路降级成 HTTP/1.1。
- **`checksum/config` 注解**。没有它，改 values 再 `helm upgrade` 只会更新 ConfigMap，跑着的 pod 还是旧路由 ——
  一次部署最令人困惑的结果。
- **`readOnlyRootFilesystem: true`** + `/tmp` 用 emptyDir。网关只写一个东西（存储），给它一个可写目录就够了。
- **ServiceAccount 不挂 token**。网关不读 K8s API，一个从不使用的凭证仍然是一个会泄露的凭证。
- **`auth.users` 按下标注入环境变量**，因为配置绑定在环境变量里没有数组语法。下标必须从 0 连续，
  中间空一个会静默截断列表 —— 少一个账号而不是报错。

## dotnet new

```bash
dotnet new install Aneiang.Yarp.Templates
dotnet new aneiang-yarp -n MyGateway --Framework net9.0
cd MyGateway && dotnet run
```

生成的项目自带 `Dockerfile`（多阶段、非 root、带探针）和一份写清了"哪些是自动的、哪些必须自己改"的 README。

---

## 验证清单

部署完之后按顺序确认这几件事，任何一条不通过就不用往下走：

```bash
# 1. 活着，并且真的 ready（ready 会检查配置是否已成功应用到 YARP）
curl -fsS http://<host>:5200/health && curl -fsS http://<host>:5200/ready

# 2. 面板能登录，返回的 token 里带 role
curl -s -X POST http://<host>:5200/aneiang/login \
  -H "Content-Type: application/json" -d '{"username":"admin","password":"<密码>"}'

# 3. 角色被正确解析（isImplicitRole 为 true 说明认证其实没开或用的是 API Key）
curl -s http://<host>:5200/aneiang/api/auth/status -H "Authorization: Bearer <token>"

# 4. 存储确实是持久的：在面板里建一条路由，重启 pod，路由还在
```
