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
/// The <c>pdp spoke</c> command tree (contracts/cli-surface.md §1). <c>spoke create</c> is the
/// one-command vend: no <c>--cidr</c> (allocated live from the ledger). US2 adds the Article VIII gate —
/// create runs the plan first, surfaces it, and applies only after confirmation (<c>--yes</c>
/// pre-confirms); <c>spoke destroy</c> requires an explicit <c>--confirm &lt;name&gt;</c> (no
/// <c>--yes</c> bypass) and releases the IPAM allocation on success.
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

    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Builds the <c>spoke</c> command, reading the recursive <paramref name="jsonOption"/>.</summary>
    public static Command Create(IServiceProvider services, Option<bool> jsonOption)
    {
        var spoke = new Command("spoke", "Vend and manage spokes.");
        spoke.Subcommands.Add(BuildCreate(services, jsonOption));
        spoke.Subcommands.Add(BuildDestroy(services, jsonOption));
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
            Description = "Pre-confirm the apply after the plan is shown (acceptable for create; destroy never has a bypass).",
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Dispatch the apply and return immediately, printing the env_id and run handle.",
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
            var yes = parseResult.GetValue(yesOption);

            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

            try
            {
                // --no-wait: dispatch the apply and return immediately, leaving completion to the Api
                // host's reconciler (contracts/cli-surface.md §4).
                if (noWait)
                {
                    var dispatched = await verbs
                        .CreateAsync(request, Confirmation.ForApply(), cancellationToken)
                        .ConfigureAwait(false);
                    VerbResultView.Render(dispatched, asJson, Console.Out);
                    return dispatched.Status == EnvironmentStatus.Failed ? ExitRunFailed : ExitSuccess;
                }

                // Article VIII two-phase gate: plan → surface → confirm → apply → track.
                var plan = await verbs.PlanCreateAsync(request, cancellationToken).ConfigureAwait(false);
                var planned = await CompletionPoller
                    .AwaitPlanAsync(scope.ServiceProvider, plan, PollTimeout, PollInterval, cancellationToken)
                    .ConfigureAwait(false);
                PlanResultView.Render(planned.Plan, asJson, Console.Out);

                if (!planned.Succeeded)
                {
                    Console.Error.WriteLine("error: the plan run did not succeed; no apply dispatched.");
                    return ExitRunFailed;
                }

                if (!yes && !Confirm("Proceed with apply?"))
                {
                    Console.Out.WriteLine("Aborted; no apply dispatched.");
                    return ExitSuccess;
                }

                var result = await verbs
                    .CreateAsync(request, Confirmation.ForApply(), cancellationToken)
                    .ConfigureAwait(false);
                result = await CompletionPoller
                    .AwaitTerminalAsync(scope.ServiceProvider, result, PollTimeout, PollInterval, cancellationToken)
                    .ConfigureAwait(false);

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

    private static Command BuildDestroy(IServiceProvider services, Option<bool> jsonOption)
    {
        var subscriptionOption = new Option<string>("--subscription", "-s")
        {
            Description = "Subscription id the spoke lives in.",
            Required = true,
        };
        var nameOption = new Option<string>("--name", "-n")
        {
            Description = "Spoke name to destroy.",
            Required = true,
        };
        var confirmOption = new Option<string>("--confirm")
        {
            Description = "Restate the spoke name to confirm the destroy (mandatory; no --yes bypass — FR-007).",
            Required = true,
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Dispatch the destroy and return immediately, printing the env_id and run handle.",
        };

        var destroy = new Command("destroy", "Destroy one spoke (releases its IPAM allocation on success).")
        {
            subscriptionOption,
            nameOption,
            confirmOption,
            noWaitOption,
        };

        destroy.SetAction(async (parseResult, cancellationToken) =>
        {
            var subscription = parseResult.GetValue(subscriptionOption)!;
            var name = parseResult.GetValue(nameOption)!;
            var confirm = parseResult.GetValue(confirmOption)!;
            var asJson = parseResult.GetValue(jsonOption);
            var noWait = parseResult.GetValue(noWaitOption);

            var target = EnvRef.ByNaturalKey(EnvironmentKind.Spoke, subscription, name);
            var confirmation = Confirmation.ForDestroy(confirm);

            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

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
                return result.Status == EnvironmentStatus.Failed ? ExitRunFailed : ExitSuccess;
            }
            catch (ConfirmationRequiredException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return ExitValidation;
            }
            catch (EnvironmentNotFoundException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
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

        return destroy;
    }

    /// <summary>Prompts the owner for a yes/no confirmation on the console (default no).</summary>
    private static bool Confirm(string prompt)
    {
        Console.Out.Write($"{prompt} [y/N] ");
        var answer = Console.In.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
