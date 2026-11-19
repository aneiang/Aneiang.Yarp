# AneiangYarpGateway

An API gateway on [YARP](https://microsoft.github.io/reverse-proxy/) with a management dashboard,
dynamic routes and plugin bindings.

```bash
dotnet run
```

| | |
|:--|:--|
| Gateway | http://localhost:5200 |
| Dashboard | http://localhost:5200/aneiang/login |
| Health | http://localhost:5200/health · `/ready` · `/live` |
| gRPC (client self-registration) | http://localhost:5201 |

The login password is `Gateway:Dashboard:Auth:JwtPassword` in `appsettings.json`. **Change it before
this gateway is reachable from anywhere.**

## Where things live

| What | Where |
|:--|:--|
| Routes and clusters | The store, edited from the dashboard. `appsettings.json` is not the source of truth once the gateway has started — anything you put in `ReverseProxy` is imported once and then owned by the store |
| Plugin configuration | Dashboard → Plugin Center. Plugins are bound per route or per cluster, not globally |
| Accounts and roles | `Gateway:Dashboard:Auth:Users`. See [`ADR-004`](https://github.com/aneiangsoft/Aneiang.Yarp/blob/master/docs/adr/ADR-004-rbac-three-roles.md) |
| The store file | `gateway-store.db` next to the binary, or `Data Source=…` in `Gateway:Storage:Sqlite:ConnectionString` |

## Adding a route without the dashboard

The dashboard is a client of the admin API, not a separate plane — anything it does can be scripted.
These are the same calls it makes:

```bash
# 1. Log in. The token carries this account's role, so a viewer token is refused on every write.
TOKEN=$(curl -s -X POST http://localhost:5200/aneiang/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"change-me-before-you-expose-this"}' \
  | sed -E 's/.*"token":"([^"]+)".*/\1/')

# 2. A cluster. Note `destinations` is an object keyed by destination name, not an array.
curl -s -X PUT http://localhost:5200/aneiang/api/config/clusters/example \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"clusterId":"example","destinations":{"primary":{"address":"https://example.com/"}}}'

# 3. A route pointing at it
curl -s -X PUT http://localhost:5200/aneiang/api/config/routes/example \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"routeId":"example","clusterId":"example","match":{"path":"/example/{**catch-all}"}}'
```

Both are `PUT` and idempotent: the same call twice is the same configuration, not two objects.
Every write is validated before it reaches the live config — a route pointing at a cluster that does
not exist is rejected here rather than failing at request time where nobody can see why.

## Turning on client self-registration

Off by default: the registration API lets a caller create a route, which is the last thing you want
open before the gateway has an auth story.

```csharp
builder.Services.AddAneiangYarp(enableRegistration: true);  // Program.cs
builder.Services.AddGatewayApiAuth();                       // reuse the dashboard credentials
```

## Taking it to production

- **Change `Auth:JwtPassword`** and set `Auth:JwtSecret` to a fixed value. Left empty, the signing key
  is generated per process — every restart logs everyone out (and breaks a multi-instance setup).
- **Keep the store outside the container.** The generated `Dockerfile` mounts a volume at `/app/data`
  and points the connection string there.
- **One replica, one store.** The config store is a single SQLite file; two replicas are two divergent
  copies of the routes. Scale the data plane with `Deployment:Mode: Split` or by running more gateway
  processes against one control plane, not by scaling this Deployment.
- **Helm users**: the chart in the Aneiang.Yarp repository covers probes, PVC retention, RBAC-friendly
  security context and the generated-credential flow.

## Plugins

The three package references in the `.csproj` give you routing, storage and the dashboard. The
dashboard package references every built-in plugin, because configuring one from the UI means knowing
its schema — so with the dashboard in place, WAF, rate-limit, retry, circuit-breaker, cache,
compression, service-discovery, metrics, proxy-log and fault-injection are all present and configurable
from the Plugin Center.

A gateway without the dashboard (a pure data plane fed over gRPC) can reference only what it runs:

```bash
dotnet add package Aneiang.Yarp.Plugin.Waf
dotnet add package Aneiang.Yarp.Plugin.RateLimit
dotnet add package Aneiang.Yarp.Plugin.CircuitBreaker
```

Either way a plugin does nothing until it is bound to a route or a cluster.
