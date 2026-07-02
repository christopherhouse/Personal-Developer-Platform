// Pdp.ControlPlane.Api — the internal ASP.NET Core host (spec 006), built host-ready for spec 007.
//
// US5 (T066): hosts the full control-plane verb layer (AddControlPlaneVerbs → IPAM ledger + registry +
// dispatch/track + telemetry/UseAzureMonitor) on the Wolverine durable outbox/inbox, the recurring
// reconciler (ReconcilerScheduler), and the GitHub workflow_run webhook endpoint. The webhook is the
// low-latency completion path; the polling reconciler guarantees completion even when a delivery is
// missed (SC-006). This host runs no OpenTofu in-process (Article II) — it only dispatches and tracks.
// Spec 007 deploys it to ACA behind the Pdp.ControlPlane.Ingress YARP proxy (the one public surface).
using Azure.Core;
using Azure.Identity;
using Npgsql;
using Octokit.Webhooks;
using Octokit.Webhooks.AspNetCore;
using Pdp.ControlPlane.Api.Webhooks;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Registry.Catalog;
using Pdp.ControlPlane.Verbs;
using Pdp.ControlPlane.Verbs.Hosting;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

// When launched by the Aspire AppHost (local dev) Postgres arrives as ConnectionStrings:pdp — let it win
// over any configured ControlPlane value so the same binary works standalone and under Aspire (mirrors
// the CLI host wiring).
var aspireConnection = builder.Configuration.GetConnectionString("pdp");
if (!string.IsNullOrWhiteSpace(aspireConnection))
{
    builder.Configuration["ControlPlane:PostgresConnectionString"] = aspireConnection;
}

// Application Insights arrives as the conventional Azure env var on the container app; surface it into the
// ControlPlane option the verb-layer telemetry binds (UseAzureMonitor). Empty (local dev / no sink) → the
// telemetry wiring stays a graceful no-op. (The env var itself is set on the api app in T049.)
var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
{
    builder.Configuration["ControlPlane:ApplicationInsightsConnectionString"] = appInsightsConnectionString;
}

var controlPlaneOptions = builder.Configuration
    .GetSection(ControlPlaneOptions.SectionName)
    .Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();

// In Azure (spec 007) the api runs under uami-api: it reaches the private, Entra-only ledger with a
// managed-identity token instead of a password (research §5). When ManagedIdentity:ClientId is set (the
// host stack pins uami-api's client id) build a token-authenticated NpgsqlDataSource via the shared
// EntraPostgres seam; otherwise (local dev / Aspire) fall back to DefaultAzureCredential and the plain
// connection-string path. The same credential serves ARG (inventory) and any KV reads.
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

// The verb layer (persistence, IPAM, dispatch/track, inventory, telemetry) + the Wolverine durable
// inbox/outbox + EF Core saga storage — the same pairing the CLI uses. The token data source — when
// present — backs the IPAM + registry DbContexts; the credential is registered last so it wins over the
// verb layer's DefaultAzureCredential default.
builder.Services.AddControlPlaneVerbs(builder.Configuration, postgres);
builder.Services.AddSingleton(credential);

// This is the SOLE run-tracking node (research §13): it keeps the FULL durability — the recurring
// reconciler/scheduled agents run here (runScheduledAgents defaults true), unlike the scale-to-zero MCP
// node which disables them.
builder.UseWolverine(opts =>
{
    if (postgres is not null)
    {
        opts.ConfigureControlPlaneMessaging(postgres);
    }
    else
    {
        opts.ConfigureControlPlaneMessaging(controlPlaneOptions.PostgresConnectionString);
    }
});

// The webhook handler is resolved per request by MapGitHubWebhooks; scoped so it can take the scoped
// Wolverine IMessageBus. It only enqueues to the durable inbox — no business logic (defense in depth).
builder.Services.AddScoped<WebhookEventProcessor, GitHubWebhookHandler>();

// Project the baked-in archetypes/catalog.json into the registry at startup (spec 008, R1). This host
// is the catalog's SOLE writer — the MCP app reads the projected tables and never syncs (no dual
// writers). A rejected/invalid file is loud but non-fatal: the previous projection keeps serving.
builder.Services.Configure<CatalogSyncOptions>(builder.Configuration.GetSection(CatalogSyncOptions.SectionName));
builder.Services.AddHostedService<CatalogSyncService>();

// Seed the recurring reconcile sweep on startup; RunReconciler reschedules itself thereafter (SC-006).
builder.Services.AddHostedService<ReconcilerScheduler>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("pdp control-plane api (spec 006)"));

// The internal workflow_run webhook endpoint. MapGitHubWebhooks validates X-Hub-Signature-256 against
// the configured secret and deserializes the typed payload before GitHubWebhookHandler runs; an unsigned
// or forged delivery is rejected here. The Pdp.ControlPlane.Ingress YARP proxy forwards public traffic
// to this path; the handler has no direct public exposure (contracts/dispatch-and-tracking.md §4).
var githubOptions = builder.Configuration
    .GetSection(GitHubAppOptions.SectionName)
    .Get<GitHubAppOptions>() ?? new GitHubAppOptions();
app.MapGitHubWebhooks("/webhooks/github", githubOptions.WebhookSecret);

app.Run();

/// <summary>Exposed so the API host tests can boot it with <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
