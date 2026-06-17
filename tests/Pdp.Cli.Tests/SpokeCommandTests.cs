using System.CommandLine;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Pdp.Cli.Commands;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;

namespace Pdp.Cli.Tests;

/// <summary>
/// The <c>pdp spoke create</c> CLI surface over a faked verb layer (T040): argument parsing (no
/// <c>--cidr</c>), the human and <c>--json</c> renderings of the one typed <see cref="VerbResult"/>
/// (SC-008), and the exit-code contract (contracts/cli-surface.md §3). Runs with <c>--no-wait</c> so no
/// Postgres/Azure is required — the verb layer is an NSubstitute double.
/// </summary>
public sealed class SpokeCommandTests
{
    private const string Subscription = "66666666-6666-6666-6666-666666666666";

    [Fact]
    public async Task Create_parses_inputs_and_renders_the_result_human_readable()
    {
        var verbs = Substitute.For<ISpokeVerbs>();
        var envId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        verbs.CreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(new VerbResult(envId, EnvironmentStatus.Provisioning, runId, null, RunOutcome.Dispatched,
                new[] { "Allocated 10.2.0.0/24 for spoke 'app1' in westus3." }));

        var (exit, stdout, _) = await RunAsync(verbs,
            "spoke", "create", "-s", Subscription, "-r", "westus3", "-n", "app1", "--no-wait");

        exit.ShouldBe(SpokeCommand.ExitSuccess);
        stdout.ShouldContain(envId.ToString());
        stdout.ShouldContain("Provisioning");
        stdout.ShouldContain("Allocated 10.2.0.0/24");

        await verbs.Received(1).CreateAsync(
            Arg.Is<SpokeCreateRequest>(r =>
                r.Subscription == Subscription && r.Region == "westus3" && r.Name == "app1" && r.Size == 24),
            Arg.Any<Confirmation>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_with_json_emits_the_typed_result_as_json()
    {
        var verbs = Substitute.For<ISpokeVerbs>();
        var envId = Guid.CreateVersion7();
        verbs.CreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(new VerbResult(envId, EnvironmentStatus.Provisioning, Guid.CreateVersion7(), null,
                RunOutcome.Dispatched, Array.Empty<string>()));

        var (exit, stdout, _) = await RunAsync(verbs,
            "spoke", "create", "-s", Subscription, "-r", "westus3", "-n", "app1", "--no-wait", "--json");

        exit.ShouldBe(SpokeCommand.ExitSuccess);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("envId").GetGuid().ShouldBe(envId);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Provisioning");
    }

    [Fact]
    public async Task Create_with_a_failed_run_exits_nonzero()
    {
        var verbs = Substitute.For<ISpokeVerbs>();
        verbs.CreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(new VerbResult(Guid.CreateVersion7(), EnvironmentStatus.Failed, Guid.CreateVersion7(), null,
                RunOutcome.Failed, new[] { "Apply run failed." }));

        var (exit, _, _) = await RunAsync(verbs,
            "spoke", "create", "-s", Subscription, "-r", "westus3", "-n", "app1", "--no-wait");

        exit.ShouldBe(SpokeCommand.ExitRunFailed);
    }

    [Fact]
    public async Task Create_on_validation_failure_exits_two()
    {
        var verbs = Substitute.For<ISpokeVerbs>();
        verbs.CreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ValidationException(new[] { new ValidationFailure("Name", "bad name") }));

        var (exit, _, stderr) = await RunAsync(verbs,
            "spoke", "create", "-s", Subscription, "-r", "westus3", "-n", "BAD", "--no-wait");

        exit.ShouldBe(SpokeCommand.ExitValidation);
        stderr.ShouldContain("bad name");
    }

    [Fact]
    public async Task Create_on_single_flight_rejection_exits_three()
    {
        var verbs = Substitute.For<ISpokeVerbs>();
        verbs.CreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationInProgressException(Guid.CreateVersion7(), EnvironmentStatus.Provisioning));

        var (exit, _, stderr) = await RunAsync(verbs,
            "spoke", "create", "-s", Subscription, "-r", "westus3", "-n", "app1", "--no-wait");

        exit.ShouldBe(SpokeCommand.ExitInProgress);
        stderr.ShouldContain("in progress");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        ISpokeVerbs verbs,
        params string[] args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(verbs);
        await using var provider = services.BuildServiceProvider();

        var jsonOption = new Option<bool>("--json") { Recursive = true };
        var root = new RootCommand("pdp test root");
        root.Options.Add(jsonOption);
        root.Subcommands.Add(SpokeCommand.Create(provider, jsonOption));

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
