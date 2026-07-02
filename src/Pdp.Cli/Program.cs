// pdp — the Personal Developer Platform control-plane CLI (spec 006).
//
// US1: hosts the verb layer in-process under the owner's context (DefaultAzureCredential is wired by
// AddControlPlaneVerbs), constructed against the platform Postgres (IPAM ledger + registry schema) and
// the GitHub App credential. A mutating command dispatches, then polls its own result to terminal
// (research §1) — the durable, continuously-running reconciler lives in the spec-007 Api host.
using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pdp.Cli.Commands;
using Pdp.ControlPlane.Verbs;
using Wolverine;

// Content root = the assembly directory so appsettings*.json (copied next to the exe) load regardless
// of the working directory `dotnet run`/the Aspire host launches us from.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Always layer the gitignored local-dev overlay on top (optional) so a single file supplies the local
// Postgres/GitHub config without per-session env vars. CreateApplicationBuilder only auto-loads it when
// the environment is Development; layering it explicitly makes `dotnet run` pick it up too.
builder.Configuration.AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false);

// When launched by the Aspire AppHost, Postgres arrives as ConnectionStrings:pdp — let it win over any
// configured ControlPlane value so the same binary works both standalone and under Aspire (forward-
// compatible with the spec-007 host wiring).
var aspireConnection = builder.Configuration.GetConnectionString("pdp");
if (!string.IsNullOrWhiteSpace(aspireConnection))
{
    builder.Configuration["ControlPlane:PostgresConnectionString"] = aspireConnection;
}

// Help, version, parse-only, and the host-light `migrate` setup command must not require a live
// Postgres/GitHub or the Wolverine messaging host — only real verb execution starts it.
var isHostlessInvocation = args.Length == 0 ||
    args.Any(a => a is "-h" or "--help" or "--version" or "-?");
var isMigrate = args is ["migrate", ..];

var controlPlaneOptions = builder.Configuration
    .GetSection(ControlPlaneOptions.SectionName)
    .Get<ControlPlaneOptions>() ?? new ControlPlaneOptions();

builder.Services.AddControlPlaneVerbs(builder.Configuration);
builder.UseWolverine(opts => opts.ConfigureControlPlaneMessaging(controlPlaneOptions.PostgresConnectionString));

using var host = builder.Build();

var jsonOption = new Option<bool>("--json")
{
    Description = "Render the typed result as JSON instead of a human table (SC-008).",
    Recursive = true,
};
var verboseOption = new Option<bool>("--verbose")
{
    Description = "Verbose diagnostics.",
    Recursive = true,
};

var root = new RootCommand("pdp — Personal Developer Platform control plane (spec 006).");
root.Options.Add(jsonOption);
root.Options.Add(verboseOption);
root.Subcommands.Add(SpokeCommand.Create(host.Services, jsonOption));
root.Subcommands.Add(WorkloadCommand.Create(host.Services, jsonOption));
root.Subcommands.Add(FabricCommand.Create(host.Services, jsonOption, controlPlaneOptions.PlatformSubscriptionId));
root.Subcommands.Add(IpamCommand.Create(host.Services, jsonOption));
root.Subcommands.Add(InventoryCommand.Create(host.Services, jsonOption));
root.Subcommands.Add(EnvCommand.Create(host.Services, jsonOption));
root.Subcommands.Add(RunCommand.Create(host.Services, jsonOption));
root.Subcommands.Add(MigrateCommand.Create(controlPlaneOptions.PostgresConnectionString));

var parseResult = root.Parse(args);

if (isHostlessInvocation || isMigrate)
{
    return await parseResult.InvokeAsync().ConfigureAwait(false);
}

// Start the messaging host so the durable outbox dispatches and the poller's reconcile sweeps run.
await host.StartAsync().ConfigureAwait(false);
try
{
    return await parseResult.InvokeAsync().ConfigureAwait(false);
}
finally
{
    await host.StopAsync().ConfigureAwait(false);
}
