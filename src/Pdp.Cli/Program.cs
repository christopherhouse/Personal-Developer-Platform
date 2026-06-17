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

// Help, version, and parse-only invocations must not require a live Postgres/GitHub — only real verb
// execution starts the messaging host.
var isHostlessInvocation = args.Length == 0 ||
    args.Any(a => a is "-h" or "--help" or "--version" or "-?");

var builder = Host.CreateApplicationBuilder(args);
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

var parseResult = root.Parse(args);

if (isHostlessInvocation)
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
