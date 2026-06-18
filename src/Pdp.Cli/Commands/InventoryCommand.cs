using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Pdp.Cli.Rendering;
using Pdp.ControlPlane.Verbs.Handlers;

namespace Pdp.Cli.Commands;

/// <summary>
/// The <c>pdp inventory</c> command (contracts/cli-surface.md §1): the full live snapshot of what PDP
/// manages, derived from Azure Resource Graph via <see cref="IInventoryVerbs"/> (spec 005 reused —
/// FR-013). "What's deployed?" comes from ARG, never the intent registry (FR-016).
/// </summary>
public static class InventoryCommand
{
    /// <summary>Builds the <c>inventory</c> command.</summary>
    public static Command Create(IServiceProvider services, Option<bool> jsonOption)
    {
        var inventory = new Command("inventory", "Show the live inventory snapshot (from Azure Resource Graph).");

        inventory.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(jsonOption);
            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IInventoryVerbs>();

            var snapshot = await verbs.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            InventoryView.RenderSnapshot(snapshot, asJson, Console.Out);
            return CliExit.Success;
        });

        return inventory;
    }
}
