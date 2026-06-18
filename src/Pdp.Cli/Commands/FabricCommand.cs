using System.CommandLine;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Pdp.Cli.Rendering;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Cli.Commands;

/// <summary>
/// The <c>pdp fabric</c> command tree (contracts/cli-surface.md §1). <c>fabric create</c> registers the
/// region in the IPAM ledger and dispatches the new <c>fabric-vend.yml</c> (FR-012a), following the
/// Article VIII gate — plan first, surface it, apply only after confirmation (<c>--yes</c> pre-confirms).
/// <c>fabric destroy</c> requires an explicit <c>--confirm &lt;region&gt;</c> (no <c>--yes</c> bypass).
/// </summary>
public static class FabricCommand
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Builds the <c>fabric</c> command. <paramref name="platformSubscriptionId"/> keys the natural key for destroy.</summary>
    public static Command Create(IServiceProvider services, Option<bool> jsonOption, string platformSubscriptionId)
    {
        var fabric = new Command("fabric", "Stand up and manage regional fabrics.");
        fabric.Subcommands.Add(BuildCreate(services, jsonOption));
        fabric.Subcommands.Add(BuildDestroy(services, jsonOption, platformSubscriptionId));
        return fabric;
    }

    private static Command BuildCreate(IServiceProvider services, Option<bool> jsonOption)
    {
        var regionOption = new Option<string>("--region", "-r")
        {
            Description = "Azure region to stand the fabric up in, e.g. westus3.",
            Required = true,
        };
        var regionIndexOption = new Option<int>("--region-index")
        {
            Description = "The region's /16 index (2nd octet; 1-255). The only address knob.",
            Required = true,
        };
        var yesOption = new Option<bool>("--yes")
        {
            Description = "Pre-confirm the apply after the plan is shown (acceptable for create).",
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Dispatch the apply and return immediately, printing the env_id and run handle.",
        };

        var create = new Command("create", "Vend one regional fabric.")
        {
            regionOption,
            regionIndexOption,
            yesOption,
            noWaitOption,
        };

        create.SetAction(async (parseResult, cancellationToken) =>
        {
            var request = new FabricCreateRequest(
                parseResult.GetValue(regionOption)!,
                parseResult.GetValue(regionIndexOption));
            var asJson = parseResult.GetValue(jsonOption);
            var noWait = parseResult.GetValue(noWaitOption);
            var yes = parseResult.GetValue(yesOption);

            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IFabricVerbs>();

            try
            {
                if (noWait)
                {
                    var dispatched = await verbs
                        .CreateAsync(request, Confirmation.ForApply(), cancellationToken)
                        .ConfigureAwait(false);
                    VerbResultView.Render(dispatched, asJson, Console.Out);
                    return dispatched.Status == EnvironmentStatus.Failed ? CliExit.RunFailed : CliExit.Success;
                }

                var plan = await verbs.PlanCreateAsync(request, cancellationToken).ConfigureAwait(false);
                var planned = await CompletionPoller
                    .AwaitPlanAsync(scope.ServiceProvider, plan, PollTimeout, PollInterval, cancellationToken)
                    .ConfigureAwait(false);
                PlanResultView.Render(planned.Plan, asJson, Console.Out);

                if (!planned.Succeeded)
                {
                    Console.Error.WriteLine("error: the plan run did not succeed; no apply dispatched.");
                    return CliExit.RunFailed;
                }

                if (!yes && !ConsolePrompt.Confirm("Proceed with apply?"))
                {
                    Console.Out.WriteLine("Aborted; no apply dispatched.");
                    return CliExit.Success;
                }

                var result = await verbs
                    .CreateAsync(request, Confirmation.ForApply(), cancellationToken)
                    .ConfigureAwait(false);
                result = await CompletionPoller
                    .AwaitTerminalAsync(scope.ServiceProvider, result, PollTimeout, PollInterval, cancellationToken)
                    .ConfigureAwait(false);

                VerbResultView.Render(result, asJson, Console.Out);
                return result.Status == EnvironmentStatus.Failed ? CliExit.RunFailed : CliExit.Success;
            }
            catch (ValidationException ex)
            {
                Console.Error.WriteLine($"error: {string.Join("; ", ex.Errors.Select(e => e.ErrorMessage))}");
                return CliExit.Validation;
            }
            catch (OperationInProgressException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return CliExit.InProgress;
            }
            catch (IpamException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return CliExit.Validation;
            }
        });

        return create;
    }

    private static Command BuildDestroy(IServiceProvider services, Option<bool> jsonOption, string platformSubscriptionId)
    {
        var regionOption = new Option<string>("--region", "-r")
        {
            Description = "Region of the fabric to destroy.",
            Required = true,
        };
        var confirmOption = new Option<string>("--confirm")
        {
            Description = "Restate the region to confirm the destroy (mandatory; no --yes bypass — FR-007).",
            Required = true,
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Dispatch the destroy and return immediately, printing the env_id and run handle.",
        };

        var destroy = new Command("destroy", "Destroy one regional fabric.")
        {
            regionOption,
            confirmOption,
            noWaitOption,
        };

        destroy.SetAction(async (parseResult, cancellationToken) =>
        {
            var region = parseResult.GetValue(regionOption)!;
            var confirm = parseResult.GetValue(confirmOption)!;
            var asJson = parseResult.GetValue(jsonOption);
            var noWait = parseResult.GetValue(noWaitOption);

            var target = EnvRef.ByNaturalKey(EnvironmentKind.Fabric, platformSubscriptionId, region);
            var confirmation = Confirmation.ForDestroy(confirm);

            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IFabricVerbs>();

            try
            {
                var result = await verbs
                    .DestroyAsync(target, confirmation, cancellationToken)
                    .ConfigureAwait(false);

                if (!noWait)
                {
                    result = await CompletionPoller
                        .AwaitTerminalAsync(scope.ServiceProvider, result, PollTimeout, PollInterval, cancellationToken)
                        .ConfigureAwait(false);
                }

                VerbResultView.Render(result, asJson, Console.Out);
                return result.Status == EnvironmentStatus.Failed ? CliExit.RunFailed : CliExit.Success;
            }
            catch (ConfirmationRequiredException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return CliExit.Validation;
            }
            catch (EnvironmentNotFoundException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return CliExit.Validation;
            }
            catch (OperationInProgressException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return CliExit.InProgress;
            }
            catch (IpamException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return CliExit.Validation;
            }
        });

        return destroy;
    }
}
