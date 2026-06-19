// pdp-mcp — the chatops MCP server (spec 007).
//
// A THIN ADAPTER: it hosts the spec-006 verb layer (Pdp.ControlPlane.Verbs) in-process — exactly as the
// `pdp` CLI does — and exposes those verbs as MCP tools over stateless streamable HTTP, gated as an Entra
// OAuth 2.1 protected resource (single allow-listed owner oid). No reimplementation, no new verb.
//
// Phase 2 (Foundational) wires the host: the verb layer + Wolverine on the private Entra-only Postgres
// (token data source, NO password), telemetry, MCP transport, and owner auth. Tools are registered later
// (T034/T046: .WithTools<SpokeTools>()…) — the server runs with zero tools until then.
using Azure.Core;
using Azure.Identity;
using Npgsql;
using Pdp.ControlPlane.Verbs;
using Pdp.ControlPlane.Verbs.Hosting;
using Pdp.Mcp.Auth;
using Pdp.Mcp.Confirm;
using Pdp.Mcp.Tools;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// Gitignored local-dev overlay (optional) so one file supplies local Postgres/Entra config without env vars.
builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);

// Application Insights arrives as the conventional Azure env var on the container app; surface it into the
// ControlPlane option the verb-layer telemetry binds (AddControlPlaneTelemetry → UseAzureMonitor). When it
// is empty (local dev / no sink), the telemetry wiring stays a graceful no-op — no Azure export attached.
var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    builder.Configuration["ControlPlane:ApplicationInsightsConnectionString"] = appInsightsConnectionString;
}

var controlPlaneOptions = builder.Configuration
    .GetSection(ControlPlaneOptions.SectionName)
    .Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();

// The per-app user-assigned managed identity (uami-mcp) is the ONE credential this node uses for every
// Azure call — the Postgres Entra token, ARG reads, Key Vault secret reads. In Azure it is pinned by
// client id (ManagedIdentity:ClientId, set by the host stack). Without a client id (local dev) fall back
// to DefaultAzureCredential (az login / the owner) and the plain connection-string Postgres path.
var uamiClientId = builder.Configuration["ManagedIdentity:ClientId"];
TokenCredential credential;
NpgsqlDataSource? postgres;
if (!string.IsNullOrWhiteSpace(uamiClientId))
{
    credential = new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(uamiClientId));
    postgres = EntraPostgres.CreateDataSource(controlPlaneOptions.PostgresConnectionString, credential);
}
else
{
    credential = new DefaultAzureCredential();
    postgres = null;
}

// Host the spec-006 verb layer in-process (validate → allocate → record → dispatch + reads). The token
// data source — when present — backs the IPAM + registry DbContexts; the same credential serves ARG and
// Key Vault (registered last so it wins over the verb layer's DefaultAzureCredential default).
builder.Services.AddControlPlaneVerbs(builder.Configuration, postgres);
builder.Services.AddSingleton(credential);

builder.UseWolverine(opts =>
{
    // runScheduledAgents: false → Serverless durability on this scale-to-zero node. It keeps the durable
    // transactional outbox (a dispatch enqueued here is durable + sent) but does NOT run the background
    // scheduled/reconciler agents — the always-on Api node owns the recurring sweep (research §13). The
    // MCP node also deliberately never registers ReconcilerScheduler (that lives in the Api host).
    if (postgres is not null)
    {
        opts.ConfigureControlPlaneMessaging(postgres, runScheduledAgents: false);
    }
    else
    {
        opts.ConfigureControlPlaneMessaging(controlPlaneOptions.PostgresConnectionString, runScheduledAgents: false);
    }
});

// The plan→confirm token store backing the Article VIII gate on the conversational surface (data-model §5).
// Singleton so a token issued by a Plan… tool is redeemable by its Apply…/Destroy… on the same replica.
builder.Services.AddSingleton<IConfirmationTokens>(sp =>
    new ConfirmationTokenService(sp.GetService<TimeProvider>()));

// MCP server over STATELESS streamable HTTP — fits ACA scale-to-zero (no session affinity; any replica or
// cold start serves any call). The verb tools are registered explicitly (not assembly-scan): US2 mutate +
// destroy (T034); US3 adds the read tools (T046). Each is a thin adapter over the spec-006 verb layer.
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<SpokeTools>()
    .WithTools<FabricTools>()
    .WithTools<IpamTools>()
    .WithTools<InventoryTools>()
    .WithTools<RunTools>();

// Entra OAuth 2.1 protected-resource auth: JWT validation + PRM publication + the single-owner oid policy.
builder.Services.AddOwnerAuthorization(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Liveness probe (no auth) for the ACA health check.
app.MapGet("/healthz", () => Results.Ok("pdp-mcp: up (spec 007)"));

// The MCP endpoint at /mcp, gated to the owner (the YARP ingress forwards /mcp path-preserving — contracts/
// hosting-topology.md). The PRM discovery document is published by AddOwnerAuthorization at
// /.well-known/oauth-protected-resource; an unauthenticated call is challenged there (401 + WWW-Authenticate).
app.MapMcp("/mcp").RequireAuthorization(OwnerAuthorization.PolicyName);

app.Run();

/// <summary>Exposed so the MCP host tests can boot it with <c>WebApplicationFactory&lt;Program&gt;</c> (T028).</summary>
public partial class Program;
