// Pdp.ControlPlane.Ingress — the one public surface (Article IX): a pure YARP reverse proxy that
// forwards the GitHub webhook to the internal Api. No business logic.
//
// Phase 1 scaffold: routes/clusters are loaded from configuration (wired in T065). The minimal app
// below builds and runs; an empty "ReverseProxy" section is a valid (no-route) configuration.
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.MapReverseProxy();

app.Run();
