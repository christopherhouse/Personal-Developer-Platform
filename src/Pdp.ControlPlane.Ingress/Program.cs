// Pdp.ControlPlane.Ingress — the one public surface (Article IX): a pure YARP reverse proxy that
// forwards POST /webhooks/github to the internal Api. No business logic, no auth termination — the Api's
// MapGitHubWebhooks validates the HMAC signature (defense in depth; contracts/dispatch-and-tracking.md §4).
//
// Routes/clusters come from the "ReverseProxy" config section (appsettings.json); the single route
// matches POST /webhooks/github and the destination Address is overridden by the Aspire AppHost for local
// dev and by spec-007 ACA config in production. Spec 007 deploys this container to ACA with the public
// ingress + managed identity (FR-019).
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// Azure Monitor OpenTelemetry on the edge (true e2e tracing). Without this the ingress was invisible in
// App Insights and every backend trace started at the api/mcp hop with no parent. With UseAzureMonitor the
// inbound request here becomes the TRACE ROOT, and YARP propagates the W3C `traceparent` on the forwarded
// request — so the api/mcp spans (which already export via the verb layer's UseAzureMonitor) nest under
// this one and a webhook/mcp call is a single end-to-end transaction (ingress -> api|mcp). AddSource pulls
// in YARP 2.x's forwarder ActivitySource so the proxy hop shows as its own span. The connection string is
// the conventional Azure env var, injected on the container app (infra/control-plane-host); when it is
// empty (local dev / no sink) the whole block is skipped — a graceful no-op, exactly as api/mcp behave.
// OTEL_SERVICE_NAME=pdp-ingress (set on the container app) names the node in the application map.
var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    builder.Services
        .AddOpenTelemetry()
        .UseAzureMonitor(o => o.ConnectionString = appInsightsConnectionString)
        .WithTracing(tracing => tracing.AddSource("Yarp.ReverseProxy"));
}

var app = builder.Build();

app.MapReverseProxy();

app.Run();
