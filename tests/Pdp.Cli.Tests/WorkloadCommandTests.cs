using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pdp.Cli.Commands;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;

namespace Pdp.Cli.Tests;

/// <summary>
/// Spec-008 T031 — the <c>pdp workload deploy</c> CLI surface over a faked verb layer: the
/// <c>--param</c>/<c>--parameters-file</c> merge (file wins; scalar JSON parsing with string
/// fallback), the exit-code contract (2 for validation incl. schema violations printed one per line
/// as <c>&lt;path&gt;: &lt;message&gt;</c>, 3 for single-flight), and the deploy-only <c>--yes</c>
/// semantics (exercised via <c>--no-wait</c> so no Postgres/Azure is required).
/// </summary>
[Collection(CliConsoleCollection.Name)]
public sealed class WorkloadCommandTests
{
    private const string Subscription = "99999999-9999-9999-9999-999999999999";

    // --- Parameter merging -------------------------------------------------------------------------

    [Fact]
    public void BuildParameters_parses_json_scalars_with_string_fallback()
    {
        var parameters = WorkloadCommand.BuildParameters(
            ["cpu=0.5", "publicEndpoint=true", "targetPort=8080", "memory=1Gi"],
            parametersFilePath: null);

        parameters["cpu"]!.GetValue<double>().ShouldBe(0.5);
        parameters["publicEndpoint"]!.GetValue<bool>().ShouldBeTrue();
        parameters["targetPort"]!.GetValue<int>().ShouldBe(8080);
        parameters["memory"]!.GetValue<string>().ShouldBe("1Gi"); // not valid JSON → plain string
    }

    [Fact]
    public void BuildParameters_lets_the_parameters_file_win_on_conflict()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """{"cpu": 1.0, "containerImage": "from-file:1"}""");

            var parameters = WorkloadCommand.BuildParameters(
                ["cpu=0.25", "memory=1Gi"], file);

            parameters["cpu"]!.GetValue<double>().ShouldBe(1.0);              // file wins
            parameters["containerImage"]!.GetValue<string>().ShouldBe("from-file:1");
            parameters["memory"]!.GetValue<string>().ShouldBe("1Gi");         // --param survives
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void BuildParameters_rejects_a_param_without_a_key()
    {
        Should.Throw<ArgumentException>(() =>
            WorkloadCommand.BuildParameters(["novalue"], parametersFilePath: null));
    }

    // --- Command surface ---------------------------------------------------------------------------

    [Fact]
    public async Task Deploy_parses_inputs_and_calls_the_verb_with_the_merged_parameters()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        var envId = Guid.CreateVersion7();
        verbs.DeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(new VerbResult(envId, EnvironmentStatus.Provisioning, Guid.CreateVersion7(), null,
                RunOutcome.Dispatched, new[] { "Deploying workload 'demo-api'." }));

        var (exit, stdout, _) = await RunAsync(verbs,
            "workload", "deploy", "-s", Subscription, "--spoke", "app1", "-n", "demo-api",
            "--archetype", "container-app-sql", "--env", "dev",
            "--param", "containerImage=nginx:latest", "--param", "cpu=0.5", "--no-wait");

        exit.ShouldBe(SpokeCommand.ExitSuccess);
        stdout.ShouldContain(envId.ToString());

        await verbs.Received(1).DeployAsync(
            Arg.Is<WorkloadDeployRequest>(r =>
                r.Subscription == Subscription &&
                r.SpokeName == "app1" &&
                r.WorkloadName == "demo-api" &&
                r.Archetype == "container-app-sql" &&
                r.Environment == "dev" &&
                r.Parameters["containerImage"]!.GetValue<string>() == "nginx:latest" &&
                r.Parameters["cpu"]!.GetValue<double>() == 0.5),
            Arg.Is<Confirmation>(c => c.IsConfirmed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deploy_with_json_emits_the_typed_result()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        var envId = Guid.CreateVersion7();
        verbs.DeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(new VerbResult(envId, EnvironmentStatus.Provisioning, Guid.CreateVersion7(), null,
                RunOutcome.Dispatched, Array.Empty<string>()));

        var (exit, stdout, _) = await RunAsync(verbs,
            "workload", "deploy", "-s", Subscription, "--spoke", "app1", "-n", "demo-api",
            "--archetype", "container-app-sql", "--env", "dev",
            "--param", "containerImage=nginx:latest", "--no-wait", "--json");

        exit.ShouldBe(SpokeCommand.ExitSuccess);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("envId").GetGuid().ShouldBe(envId);
    }

    [Fact]
    public async Task Deploy_on_schema_violation_exits_two_and_prints_one_line_per_parameter()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        verbs.DeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new WorkloadParameterValidationException("container-app-sql", "v1.0.0",
            [
                new ParameterViolation("/cpu", "enum", "value is not one of 0.25, 0.5, 1"),
                new ParameterViolation("(root)", "required", "containerImage is required"),
            ]));

        var (exit, _, stderr) = await RunAsync(verbs,
            "workload", "deploy", "-s", Subscription, "--spoke", "app1", "-n", "demo-api",
            "--archetype", "container-app-sql", "--env", "dev", "--param", "cpu=3", "--no-wait");

        exit.ShouldBe(SpokeCommand.ExitValidation);
        stderr.ShouldContain("/cpu: value is not one of 0.25, 0.5, 1");
        stderr.ShouldContain("(root): containerImage is required");
    }

    [Fact]
    public async Task Deploy_on_shape_validation_failure_exits_two()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        verbs.DeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ValidationException(new[] { new ValidationFailure("WorkloadName", "bad name") }));

        var (exit, _, stderr) = await RunAsync(verbs,
            "workload", "deploy", "-s", Subscription, "--spoke", "app1", "-n", "BAD",
            "--archetype", "container-app-sql", "--env", "dev", "--no-wait");

        exit.ShouldBe(SpokeCommand.ExitValidation);
        stderr.ShouldContain("bad name");
    }

    [Fact]
    public async Task Deploy_on_retired_archetype_exits_two_with_the_refusal()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        verbs.DeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(ArchetypeNotDeployableException.Retired("container-app-sql"));

        var (exit, _, stderr) = await RunAsync(verbs,
            "workload", "deploy", "-s", Subscription, "--spoke", "app1", "-n", "demo-api",
            "--archetype", "container-app-sql", "--env", "dev", "--no-wait");

        exit.ShouldBe(SpokeCommand.ExitValidation);
        stderr.ShouldContain("retired");
    }

    [Fact]
    public async Task Deploy_on_single_flight_rejection_exits_three()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        verbs.DeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationInProgressException(Guid.CreateVersion7(), EnvironmentStatus.Provisioning));

        var (exit, _, stderr) = await RunAsync(verbs,
            "workload", "deploy", "-s", Subscription, "--spoke", "app1", "-n", "demo-api",
            "--archetype", "container-app-sql", "--env", "dev", "--no-wait");

        exit.ShouldBe(SpokeCommand.ExitInProgress);
        stderr.ShouldContain("in progress");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        IWorkloadVerbs verbs,
        params string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(verbs);
        await using var provider = services.BuildServiceProvider();

        var jsonOption = new Option<bool>("--json") { Recursive = true };
        var root = new RootCommand("pdp test root");
        root.Options.Add(jsonOption);
        root.Subcommands.Add(WorkloadCommand.Create(provider, jsonOption));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var previousOut = Console.Out;
        var previousError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await root.Parse(args).InvokeAsync();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
