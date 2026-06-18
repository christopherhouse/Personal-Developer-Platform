using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Pdp.Cli.Rendering;
using Pdp.ControlPlane.Verbs.Handlers;

namespace Pdp.Cli.Commands;

/// <summary>
/// The <c>pdp env</c> command tree (contracts/cli-surface.md §1): the deployed environments and their
/// contents, from Azure Resource Graph via <see cref="IInventoryVerbs"/> (spec 005 reused). This is
/// "what's deployed?" (ARG), distinct from <c>pdp run</c> which reports recorded intent/history (the
/// registry) — the division of truth (FR-016). <c>env show</c> on an unknown name is a clean empty
/// result, not an error (FR-008).
/// </summary>
public static class EnvCommand
{
    /// <summary>Builds the <c>env</c> command.</summary>
    public static Command Create(IServiceProvider services, Option<bool> jsonOption)
    {
        var env = new Command("env", "List deployed environments and show their contents.");
        env.Subcommands.Add(BuildList(services, jsonOption));
        env.Subcommands.Add(BuildShow(services, jsonOption));
        return env;
    }

    private static Command BuildList(IServiceProvider services, Option<bool> jsonOption)
    {
        var list = new Command("list", "List the deployed environments.");

        list.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(jsonOption);
            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IInventoryVerbs>();

            var environments = await verbs.GetEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
            InventoryView.RenderEnvironments(environments, asJson, Console.Out);
            return CliExit.Success;
        });

        return list;
    }

    private static Command BuildShow(IServiceProvider services, Option<bool> jsonOption)
    {
        var nameArgument = new Argument<string>("name") { Description = "The environment name to show." };

        var show = new Command("show", "Show the contents of one environment.")
        {
            nameArgument,
        };

        show.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(jsonOption);
            var name = parseResult.GetValue(nameArgument)!;
            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IInventoryVerbs>();

            var environment = await verbs.GetEnvironmentAsync(name, cancellationToken).ConfigureAwait(false);
            InventoryView.RenderEnvironment(name, environment, asJson, Console.Out);
            return CliExit.Success;
        });

        return show;
    }
}
