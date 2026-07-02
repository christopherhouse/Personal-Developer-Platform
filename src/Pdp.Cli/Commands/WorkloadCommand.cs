using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Pdp.Cli.Rendering;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.Cli.Commands;

/// <summary>
/// The <c>pdp workload</c> command tree (spec 008, contracts/workload-verbs.md §CLI). <c>workload
/// deploy</c> mirrors <c>spoke create</c>'s Article VIII flow — plan → surface → confirm → apply →
/// track (<c>--yes</c> pre-confirms the deploy; destroy never has a bypass) — with the archetype's
/// parameters gathered from repeatable <c>--param key=value</c> options and/or a
/// <c>--parameters-file</c> (the file wins on conflict). Schema violations exit 2, printed one per
/// line as <c>&lt;path&gt;: &lt;message&gt;</c> (FR-003/SC-002). There is deliberately no catalog
/// mutation command: the catalog changes only by PR (clarify 2026-07-02). The <c>destroy</c>
/// subcommand lands with US2 (T040).
/// </summary>
public static class WorkloadCommand
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Builds the <c>workload</c> command, reading the recursive <paramref name="jsonOption"/>.</summary>
    public static Command Create(IServiceProvider services, Option<bool> jsonOption)
    {
        var workload = new Command("workload", "Deploy and manage workloads from the archetype catalog.");
        workload.Subcommands.Add(BuildDeploy(services, jsonOption));
        return workload;
    }

    /// <summary>
    /// Merges the archetype parameters from repeatable <c>--param key=value</c> options and an
    /// optional <c>--parameters-file</c> JSON object; the <b>file wins</b> on a key conflict.
    /// <c>--param</c> values parse as JSON scalars (numbers, booleans, quoted strings) with a plain
    /// string fallback, so <c>--param cpu=0.5</c> is a number and <c>--param memory=1Gi</c> a string.
    /// </summary>
    /// <exception cref="ArgumentException">A malformed <c>--param</c> or a non-object parameters file.</exception>
    public static JsonObject BuildParameters(IReadOnlyList<string> paramOptions, string? parametersFilePath)
    {
        var parameters = new JsonObject();

        foreach (var option in paramOptions)
        {
            var separator = option.IndexOf('=');
            if (separator <= 0)
            {
                throw new ArgumentException($"--param '{option}' must be key=value.");
            }

            var key = option[..separator];
            var value = option[(separator + 1)..];
            parameters[key] = ParseScalar(value);
        }

        if (!string.IsNullOrWhiteSpace(parametersFilePath))
        {
            JsonObject fromFile;
            try
            {
                fromFile = JsonNode.Parse(File.ReadAllText(parametersFilePath)) as JsonObject
                    ?? throw new ArgumentException($"--parameters-file '{parametersFilePath}' must contain a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new ArgumentException($"--parameters-file '{parametersFilePath}' is not valid JSON: {ex.Message}");
            }

            foreach (var (key, value) in fromFile.ToList())
            {
                // The file is the reviewed, versionable form — it wins over ad-hoc --param values.
                parameters[key] = value?.DeepClone();
            }
        }

        return parameters;
    }

    /// <summary>A <c>--param</c> value: a JSON scalar when it parses as one, otherwise a plain string.</summary>
    private static JsonNode? ParseScalar(string value)
    {
        try
        {
            var node = JsonNode.Parse(value);
            // Only scalars pass through; a composite value ({...}/[...]) belongs in --parameters-file,
            // but honoring it here costs nothing and the schema is the real gate.
            return node;
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }

    private static Command BuildDeploy(IServiceProvider services, Option<bool> jsonOption)
    {
        var subscriptionOption = new Option<string>("--subscription", "-s")
        {
            Description = "Target subscription id (Azure GUID) — the subscription the spoke lives in.",
            Required = true,
        };
        var spokeOption = new Option<string>("--spoke")
        {
            Description = "Existing Active spoke the workload deploys into.",
            Required = true,
        };
        var nameOption = new Option<string>("--name", "-n")
        {
            Description = "Workload name, unique within the subscription ([a-z0-9-], 1-24).",
            Required = true,
        };
        var archetypeOption = new Option<string>("--archetype")
        {
            Description = "Catalog archetype name, e.g. container-app-sql (version resolved server-side).",
            Required = true,
        };
        var envOption = new Option<string>("--env")
        {
            Description = "Workload environment (the pdp-env tag), e.g. dev ([a-z0-9-], 1-16).",
            Required = true,
        };
        var paramOption = new Option<string[]>("--param")
        {
            Description = "Archetype parameter as key=value (repeatable). Values parse as JSON scalars with string fallback.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var parametersFileOption = new Option<string?>("--parameters-file")
        {
            Description = "JSON object file of archetype parameters; wins over --param on conflict.",
        };
        var yesOption = new Option<bool>("--yes")
        {
            Description = "Pre-confirm the apply after the plan is shown (acceptable for deploy; destroy never has a bypass).",
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Dispatch the deploy and return immediately, printing the env_id and run handle.",
        };

        var deploy = new Command("deploy", "Deploy one workload from the archetype catalog into a spoke.")
        {
            subscriptionOption,
            spokeOption,
            nameOption,
            archetypeOption,
            envOption,
            paramOption,
            parametersFileOption,
            yesOption,
            noWaitOption,
        };

        deploy.SetAction(async (parseResult, cancellationToken) =>
        {
            var asJson = parseResult.GetValue(jsonOption);
            var noWait = parseResult.GetValue(noWaitOption);
            var yes = parseResult.GetValue(yesOption);

            JsonObject parameters;
            try
            {
                parameters = BuildParameters(
                    parseResult.GetValue(paramOption) ?? [],
                    parseResult.GetValue(parametersFileOption));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return SpokeCommand.ExitValidation;
            }

            var request = new WorkloadDeployRequest(
                parseResult.GetValue(subscriptionOption)!,
                parseResult.GetValue(spokeOption)!,
                parseResult.GetValue(nameOption)!,
                parseResult.GetValue(archetypeOption)!,
                parseResult.GetValue(envOption)!,
                parameters);

            await using var scope = services.CreateAsyncScope();
            var verbs = scope.ServiceProvider.GetRequiredService<IWorkloadVerbs>();

            try
            {
                // --no-wait: dispatch the apply and return immediately, leaving completion to the Api
                // host's reconciler.
                if (noWait)
                {
                    var dispatched = await verbs
                        .DeployAsync(request, Confirmation.ForApply(), cancellationToken)
                        .ConfigureAwait(false);
                    VerbResultView.Render(dispatched, asJson, Console.Out);
                    return dispatched.Status == EnvironmentStatus.Failed
                        ? SpokeCommand.ExitRunFailed
                        : SpokeCommand.ExitSuccess;
                }

                // Article VIII two-phase gate: plan → surface → confirm → apply → track.
                var plan = await verbs.PlanDeployAsync(request, cancellationToken).ConfigureAwait(false);
                var planned = await CompletionPoller
                    .AwaitPlanAsync(scope.ServiceProvider, plan, PollTimeout, PollInterval, cancellationToken)
                    .ConfigureAwait(false);
                PlanResultView.Render(planned.Plan, asJson, Console.Out);

                if (!planned.Succeeded)
                {
                    Console.Error.WriteLine("error: the plan run did not succeed; no apply dispatched.");
                    return SpokeCommand.ExitRunFailed;
                }

                if (!yes && !Confirm("Proceed with deploy?"))
                {
                    Console.Out.WriteLine("Aborted; no apply dispatched.");
                    return SpokeCommand.ExitSuccess;
                }

                var result = await verbs
                    .DeployAsync(request, Confirmation.ForApply(), cancellationToken)
                    .ConfigureAwait(false);
                result = await CompletionPoller
                    .AwaitTerminalAsync(scope.ServiceProvider, result, PollTimeout, PollInterval, cancellationToken)
                    .ConfigureAwait(false);

                VerbResultView.Render(result, asJson, Console.Out);
                return result.Status == EnvironmentStatus.Failed
                    ? SpokeCommand.ExitRunFailed
                    : SpokeCommand.ExitSuccess;
            }
            catch (WorkloadParameterValidationException ex)
            {
                // Schema-derived rejection (FR-003): one line per offending parameter, exit 2.
                Console.Error.WriteLine(
                    $"error: parameters do not satisfy archetype '{ex.Archetype}' {ex.Version}:");
                foreach (var violation in ex.Violations)
                {
                    Console.Error.WriteLine($"{violation.Path}: {violation.Message}");
                }

                return SpokeCommand.ExitValidation;
            }
            catch (ValidationException ex)
            {
                Console.Error.WriteLine($"error: {string.Join("; ", ex.Errors.Select(e => e.ErrorMessage))}");
                return SpokeCommand.ExitValidation;
            }
            catch (Exception ex) when (ex is ArchetypeNotDeployableException
                                           or EnvironmentNotFoundException
                                           or SpokeNotActiveException
                                           or WorkloadParametersChangedException)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return SpokeCommand.ExitValidation;
            }
            catch (OperationInProgressException ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return SpokeCommand.ExitInProgress;
            }
        });

        return deploy;
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
