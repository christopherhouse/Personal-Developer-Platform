// Pdp.ControlPlane.Api — internal ASP.NET Core host (spec 006), built host-ready for spec 007.
//
// Phase 1 scaffold: a minimal app that builds and runs. The real wiring — AddControlPlaneVerbs,
// the Wolverine durable outbox/inbox, UseAzureMonitor(), the reconciler schedule, and the GitHub
// webhook endpoint — lands with US1/US5 (T063, T066).
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("pdp control-plane api (spec 006)"));

app.Run();
