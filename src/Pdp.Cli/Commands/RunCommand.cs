using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Pdp.Cli.Rendering;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Cli.Commands;

/// <summary>
/// The <c>pdp run</c> command tree (contracts/cli-surface.md §1): the registry's answer to "what did I
/// ask for and what happened?" — an environment's recorded intent + provisioning-run audit trail
/// (<c>run list --env</c>) and a single run's detail (<c>run show &lt;run-id&gt;</c>). This reads
/// intent/history (the registry), distinct from <c>pdp inventory</c>/<c>pdp env</c> which read deployed
/// truth (ARG) — the division of truth (FR-016).
/// </summary>
public static class RunCommand
{
    /// <summary>Builds the <c>run</c> command.</summary>
    public static Command Create(IServiceProvider services, Option<bool> jsonOption)
    {
        var run = new Command("run", "Query the intent registry and provisioning-run audit trail.");
        run.Subcommands.Add(BuildList(services, jsonOption));
        run.Subcommands.Add(BuildShow(services, jsonOption));
        return run;
    }

    private static Command BuildList(IServiceProvider services, Option<bool> jsonOption)
    {
        var envOption = new Option<string>("--env", "-e")
        {
            Description = "Environment ref: an env_id (UUID) or kind:subscription:name (e.g. spoke:<sub>:app5).",
            Required = true,
        };

        var list = new Command("list", "Show an environment's recorded intent + run audit trail.")
        {
            envOption,
        };

        list.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(jsonOption);
            var envArg = parseResult.GetValue(envOption)!;

            if (!EnvRef.TryParse(envArg, out var target))
            {
                Console.Error.WriteLine(
                    $"error: '{envArg}' is not a valid env ref. Use an env_id (UUID) or kind:subscription:name.");
                return CliExit.Validation;
            }

            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IRunVerbs>();

            var environment = await verbs.GetEnvironmentAsync(target!, cancellationToken).ConfigureAwait(false);
            var runs = await verbs.GetRunsAsync(target!, cancellationToken).ConfigureAwait(false);
            RunView.RenderTrail(environment, runs, asJson, Console.Out);
            return CliExit.Success;
        });

        return list;
    }

    private static Command BuildShow(IServiceProvider services, Option<bool> jsonOption)
    {
        var runIdArgument = new Argument<string>("run-id") { Description = "The provisioning run id (UUID)." };

        var show = new Command("show", "Show one provisioning run in full.")
        {
            runIdArgument,
        };

        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(jsonOption);
            var runIdArg = parseResult.GetValue(runIdArgument)!;

            if (!Guid.TryParse(runIdArg, out var runId))
            {
                Console.Error.WriteLine($"error: '{runIdArg}' is not a valid run id (UUID).");
                return CliExit.Validation;
            }

            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IRunVerbs>();

            var run = await verbs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
            RunView.RenderRun(runIdArg, run, asJson, Console.Out);
            return CliExit.Success;
        });

        return show;
    }
}
