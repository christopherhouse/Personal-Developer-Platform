// pdp-mcp — the chatops MCP server (spec 007).
//
// A THIN ADAPTER: it hosts the spec-006 verb layer (Pdp.ControlPlane.Verbs) in-process — exactly as
// the `pdp` CLI does — and exposes those verbs as MCP tools over streamable HTTP, gated as an Entra
// OAuth 2.1 protected resource (single allow-listed owner oid).
//
// Phase 1 (Setup, T001) scaffolds this project only. The real bootstrap is implemented later:
//   - T011: AddMcpServer().WithHttpTransport(o => o.Stateless = true), AddControlPlaneVerbs(...),
//           UseAzureMonitor(); the polling reconciler stays OFF on this node (research §13).
//   - T013: AddJwtBearer (Entra v2.0) + .AddMcp PRM + the "OwnerOnly" oid policy; MapMcp().RequireAuthorization.
//   - T032-T034 / T043-T046: register the [McpServerToolType] tool classes over the verb interfaces.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Placeholder liveness endpoint so the scaffolded host runs; replaced by the MCP endpoint in Phase 2.
app.MapGet("/healthz", () => Results.Ok("pdp-mcp: scaffolded (spec 007, Phase 1)"));

app.Run();
