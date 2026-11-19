# Container image for this gateway.
#
# Build from the project directory:
#   docker build -t my-gateway:0.1 .
#   docker run -p 5200:5200 -v gw-data:/app/data my-gateway:0.1
#
# The store path is overridden below because a relative path resolves next to the binary, which is
# read-only for the non-root user this image runs as. The volume keeps routes across restarts.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY *.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# curl exists only for HEALTHCHECK: the aspnet image ships neither curl nor wget, and a probe that
# always fails because its binary is missing is worse than no probe.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && groupadd --gid 10001 gateway \
 && useradd --uid 10001 --gid gateway --create-home --shell /usr/sbin/nologin gateway \
 && mkdir -p /app/data \
 && chown -R gateway:gateway /app

WORKDIR /app
COPY --from=build --chown=gateway:gateway /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    Gateway__Storage__Sqlite__ConnectionString="Data Source=/app/data/gateway-store.db"

USER gateway
VOLUME ["/app/data"]
EXPOSE 5200 5201

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5200/health || exit 1

ENTRYPOINT ["dotnet", "AneiangYarpGateway.dll"]
