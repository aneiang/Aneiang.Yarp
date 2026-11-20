using Aneiang.Yarp.Dashboard.Extensions;
using Aneiang.Yarp.Dashboard.Infrastructure;
using Aneiang.Yarp.Dashboard.Infrastructure.Deployment;
using Aneiang.Yarp.Extensions;
using Aneiang.Yarp.Storage.Sqlite;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// HTTP/2 on every endpoint (gRPC needs it), with the +1 gRPC port derived from Kestrel config.
// Skip this if you declare your own Kestrel:Endpoints — it defers to them automatically.
builder.UseYarpKestrelAutoConfig();

// YARP itself: routes, clusters and the dynamic-config pipeline.
// enableRegistration stays false by default: the self-registration API lets a caller add a
// route, which is exactly what you do not want open before the gateway has an auth story.
// AddAneiangYarp already registers the API authorization filter — turn registration on only after
// configuring its credentials (Gateway:ApiAuth), or the endpoint accepts anonymous writes.
builder.Services.AddAneiangYarp(enableRegistration: false);

// The config store. This is the one line that decides where routes live.
builder.Services.AddAneiangStorage();

// Management dashboard + admin API. RoutePrefix and auth live under Gateway:Dashboard.
builder.Services.AddAneiangYarpDashboard();

// Deployment roles (AllInOne / Split / ProxyOnly), hot reload, health endpoints.
builder.Services.AddAneiangYarpDeployment();

var app = builder.Build();

app.UseRouting();
app.UseCors();

// Mounts the dashboard (mode-aware) and MapReverseProxy. Order matters: the dashboard and the
// admin API must be matched before the catch-all proxy route.
app.UseAneiangYarpDashboard();

app.MapControllers();
app.MapAneiangYarpGrpc();

// A one-line startup summary. Printing the resolved ports matters because the dashboard port is
// frequently not the one people expect — it is derived from Kestrel config, not hardcoded.
var deployment = app.Services.GetRequiredService<IOptions<DeploymentOptions>>().Value;
var dashboard = app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;
app.Logger.LogInformation("Aneiang.Yarp gateway starting in {Mode} mode", deployment.Mode);
foreach (var endpoint in deployment.ResolvedEndpoints)
    app.Logger.LogInformation("  {Role,-10} {IpAddress}:{Port}  ({Name})",
        endpoint.Role, endpoint.IpAddress, endpoint.Port, endpoint.EndpointName);
app.Logger.LogInformation("  Dashboard  /{Prefix}  (login at /{Prefix}/login)",
    dashboard.RoutePrefix, dashboard.RoutePrefix);

app.Run();
