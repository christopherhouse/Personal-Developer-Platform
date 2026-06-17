using System.CommandLine;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Pdp.Cli.Rendering;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Cli.Commands;

/// <summary>
/// The <c>pdp spoke</c> command tree (contracts/cli-surface.md §1). US1 wires <c>spoke create</c> — the
/// one-command vend: no <c>--cidr</c> (allocated live from the ledger), dispatches the apply, then polls
/// to a terminal outcome unless <c>--no-wait</c> is given. <c>spoke destroy</c> + the plan/confirm UX
/// land with US2.
/// </summary>
public static class SpokeCommand
{
    /// <summary>Exit code for a clean success / completed run.</summary>
    public const int ExitSuccess = 0;

    /// <summary>Exit code for a tracked run that ended <c>Failed</c>.</summary>
    public const int ExitRunFailed = 1;

    /// <summary>Exit code for invalid input / a ledger precondition failure (FR-023).</summary>
    public const int ExitValidation = 2;

    /// <summary>Exit code for a single-flight rejection (FR-022a).</summary>
    public const int ExitInProgress = 3;

    /// <summary>Builds the <c>spoke</c> command, reading the recursive <paramref name="jsonOption"/>.</summary>
    public static Command Create(IServiceProvider services, Option<bool> jsonOption)
    {
        var spoke = new Command("spoke", "Vend and manage spokes.");
        spoke.Subcommands.Add(BuildCreate(services, jsonOption));
        return spoke;
    }

    private static Command BuildCreate(IServiceProvider services, Option<bool> jsonOption)
    {
        var subscriptionOption = new Option<string>("--subscription", "-s")
        {
            Description = "Target subscription id (Azure GUID) to vend the spoke into.",
            Required = true,
        };
        var regionOption = new Option<string>("--region", "-r")
        {
            Description = "Registered region with a deployed fabric, e.g. westus3.",
            Required = true,
        };
        var nameOption = new Option<string>("--name", "-n")
        {
            Description = "Spoke name, unique within the subscription ([a-z0-9-], 1-24).",
            Required = true,
        };
        var sizeOption = new Option<int>("--size")
        {
            Description = "Block size (prefix length) to allocate from the ledger; default /24.",
            DefaultValueFactory = _ => 24,
        };
        var yesOption = new Option<bool>("--yes")
        {
            Description = "Pre-confirm the apply (acceptable for create; destroy never has a bypass).",
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Dispatch and return immediately, printing the env_id and run handle.",
        };

        var create = new Command("create", "Vend one spoke (CIDR allocated live from the IPAM ledger).")
        {
            subscriptionOption,
            regionOption,
            nameOption,
            sizeOption,
            yesOption,
            noWaitOption,
        };

        create.SetAction(async (parseResult, cancellationToken) =>
        {
            var request = new SpokeCreateRequest(
                parseResult.GetValue(subscriptionOption)!,
                parseResult.GetValue(regionOption)!,
                parseResult.GetValue(nameOption)!,
                parseResult.GetValue(sizeOption));
            var asJson = parseResult.GetValue(jsonOption);
            var noWait = parseResult.GetValue(noWaitOption);

            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

            try
            {
                var result = await verbs
                    .CreateAsync(request, Confirmation.ForApply(), cancellationToken)
                    .ConfigureAwait(false);

                if (!noWait)
                {
                    result = await CompletionPoller.AwaitTerminalAsync(
                        scope.ServiceProvider,
                        result,
                        timeout: TimeSpan.FromMinutes(20),
                        interval: TimeSpan.FromSeconds(5),
                        cancellationToken).ConfigureAwait(false);
                }

                VerbResultView.Render(result, asJson, Console.Out);
                return result.Status == EnvironmentStatus.Failed ? ExitRunFailed : ExitSuccess;
            }
            catch (ValidationException ex)
            {
                Console.Error.WriteLine($"error: {string.Join("; ", ex.Errors.Select(e => e.ErrorMessage))}");
                return ExitValidation;
            }
            catch (OperationInProgressException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitInProgress;
            }
            catch (IpamException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitValidation;
            }
        });

        return create;
    }
}
