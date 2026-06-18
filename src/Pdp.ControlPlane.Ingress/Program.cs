// Pdp.ControlPlane.Ingress — the one public surface (Article IX): a pure YARP reverse proxy that
// forwards POST /webhooks/github to the internal Api. No business logic, no auth termination — the Api's
// MapGitHubWebhooks validates the HMAC signature (defense in depth; contracts/dispatch-and-tracking.md §4).
//
// Routes/clusters come from the "ReverseProxy" config section (appsettings.json); the single route
// matches POST /webhooks/github and the destination Address is overridden by the Aspire AppHost for local
// dev and by spec-007 ACA config in production. Spec 007 deploys this container to ACA with the public
// ingress + managed identity (FR-019).
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.MapReverseProxy();

app.Run();
