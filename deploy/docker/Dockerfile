# Official reference image: a gateway you can `docker run` and have a working dashboard in 30 seconds.
#
# What this image is: the sample gateway from this repository, published as a container. It exists so
# `helm install` and `docker compose up` have something real to point at, and so the deployment chart
# is tested against an image that is actually built here rather than an imaginary one.
#
# What it is NOT: a base image to build your own gateway FROM. A gateway is a .NET project that
# references the Aneiang.Yarp packages — start one with `dotnet new aneiang-yarp` and use the
# Dockerfile that template generates. Shipping your routes and plugins as a container image you own
# is the point; inheriting someone else's binary is not.
#
# Build (context is the repository root, not this folder):
#   docker build -f deploy/docker/Dockerfile -t aneiang/yarp-gateway:2.3.0.40 .

# ── build ────────────────────────────────────────────────────────────────────────
# The SDK must be newer than the runtime: the libraries this image builds are multi-targeted
# (net8.0;net9.0;net10.0), and `dotnet restore` evaluates every one of those even when the app
# itself is net8.0 only. An 8.0 SDK stops at NETSDK1045 on the net9.0/net10.0 legs. The app
# still targets net8.0 and still runs on the 8.0 runtime below — a newer SDK is a build tool,
# not a runtime requirement.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The whole graph is copied rather than just the .csproj files: directories under src/ carry their
# own MSBuild props and the version/icon assets, and enumerating individual projects here would rot
# the moment one is added. The cost is that a source change re-runs restore.
COPY Aneiang.Yarp.sln ./
COPY src/ ./src/
COPY samples/SampleGateway/ ./samples/SampleGateway/
COPY README.md README.zh-CN.md logo.png ./
RUN dotnet restore samples/SampleGateway/SampleGateway.csproj

RUN dotnet publish samples/SampleGateway/SampleGateway.csproj \
      -c Release \
      -o /app/publish \
      --no-restore \
      /p:UseAppHost=false

# ── runtime ──────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# curl is only for HEALTHCHECK. The aspnet image ships neither curl nor wget, and a health check that
# silently reports "unhealthy" because the probe binary is missing is worse than no health check.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# Non-root. The gateway writes exactly one thing (the SQLite store), so give it one writable
# directory and nothing else.
RUN groupadd --gid 10001 gateway \
 && useradd --uid 10001 --gid gateway --create-home --shell /usr/sbin/nologin gateway \
 && mkdir -p /app/data \
 && chown -R gateway:gateway /app

WORKDIR /app
COPY --from=build --chown=gateway:gateway /app/publish ./

# Config is environment-first: everything under Gateway:* can be overridden with `__` separators.
# The store path is absolute on purpose — a relative path would land next to the binary, which is
# read-only for the app user, and the failure would only appear on the first save.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Gateway__Storage__Sqlite__ConnectionString="Data Source=/app/data/gateway-store.db"

USER gateway
VOLUME ["/app/data"]

# Gateway traffic and gRPC (client self-registration). The dashboard shares the gateway port under
# its RoutePrefix, so there is no third port to expose.
EXPOSE 5200 5201

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5200/health || exit 1

ENTRYPOINT ["dotnet", "SampleGateway.dll"]
