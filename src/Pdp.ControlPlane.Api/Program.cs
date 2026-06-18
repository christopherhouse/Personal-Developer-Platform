// Pdp.ControlPlane.Api — the internal ASP.NET Core host (spec 006), built host-ready for spec 007.
//
// US5 (T066): hosts the full control-plane verb layer (AddControlPlaneVerbs → IPAM ledger + registry +
// dispatch/track + telemetry/UseAzureMonitor) on the Wolverine durable outbox/inbox, the recurring
// reconciler (ReconcilerScheduler), and the GitHub workflow_run webhook endpoint. The webhook is the
// low-latency completion path; the polling reconciler guarantees completion even when a delivery is
// missed (SC-006). This host runs no OpenTofu in-process (Article II) — it only dispatches and tracks.
// Spec 007 deploys it to ACA behind the Pdp.ControlPlane.Ingress YARP proxy (the one public surface).
using Octokit.Webhooks;
using Octokit.Webhooks.AspNetCore;
using Pdp.ControlPlane.Api.Webhooks;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Verbs;
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

var controlPlaneOptions = builder.Configuration
    .GetSection(ControlPlaneOptions.SectionName)
    .Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();

// The verb layer (persistence, IPAM, dispatch/track, inventory, telemetry) + the Wolverine durable
// inbox/outbox + EF Core saga storage — the same pairing the CLI uses.
builder.Services.AddControlPlaneVerbs(builder.Configuration);
builder.UseWolverine(opts => opts.ConfigureControlPlaneMessaging(controlPlaneOptions.PostgresConnectionString));

// The webhook handler is resolved per request by MapGitHubWebhooks; scoped so it can take the scoped
// Wolverine IMessageBus. It only enqueues to the durable inbox — no business logic (defense in depth).
builder.Services.AddScoped<WebhookEventProcessor, GitHubWebhookHandler>();

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
