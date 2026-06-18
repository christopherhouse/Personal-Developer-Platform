using System.CommandLine;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Pdp.Cli.Commands;
using Pdp.ControlPlane.Inventory;
using Pdp.ControlPlane.Inventory.Model;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;

namespace Pdp.Cli.Tests;

/// <summary>
/// The US3 verb command surfaces over a faked verb layer (T056): <c>pdp fabric|ipam|inventory|env</c>
/// parse their inputs and render the one typed result both human-readable and as <c>--json</c> from the
/// identical object (SC-008, contracts/cli-surface.md §3). No Azure/Postgres — the verb layer is an
/// NSubstitute double.
/// </summary>
[Collection(CliConsoleCollection.Name)]
public sealed class VerbCommandsTests
{
    private const string PlatformSub = "8bd05b2f-62c5-4def-9869-f0617ebb3970";

    [Fact]
    public async Task Fabric_create_parses_inputs_and_renders_the_result()
    {
        var verbs = Substitute.For<IFabricVerbs>();
        var envId = Guid.CreateVersion7();
        verbs.CreateAsync(Arg.Any<FabricCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(new VerbResult(envId, EnvironmentStatus.Provisioning, Guid.CreateVersion7(), null,
                RunOutcome.Dispatched, new[] { "Registered region 'westus3' (index 2)." }));

        var (exit, stdout, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => FabricCommand.Create(sp, json, PlatformSub),
            "fabric", "create", "-r", "westus3", "--region-index", "2", "--no-wait");

        exit.ShouldBe(CliExit.Success);
        stdout.ShouldContain(envId.ToString());
        stdout.ShouldContain("Provisioning");

        await verbs.Received(1).CreateAsync(
            Arg.Is<FabricCreateRequest>(r => r.Region == "westus3" && r.RegionIndex == 2),
            Arg.Any<Confirmation>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ipam_allocate_emits_the_allocation_as_json()
    {
        var verbs = Substitute.For<IIpamVerbs>();
        verbs.AllocateAsync("westus3", "app1", 24, Arg.Any<CancellationToken>())
            .Returns(new Allocation { Name = "app1", Network = IPNetwork.Parse("10.2.0.0/24"), PrefixLength = 24 });

        var (exit, stdout, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => IpamCommand.Create(sp, json),
            "ipam", "allocate", "-r", "westus3", "-n", "app1", "--json");

        exit.ShouldBe(CliExit.Success);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("app1");
        // The IPNetwork converter emits the canonical CIDR string, not the struct shape.
        doc.RootElement.GetProperty("network").GetString().ShouldBe("10.2.0.0/24");
    }

    [Fact]
    public async Task Ipam_query_renders_one_region_human_readable()
    {
        var verbs = Substitute.For<IIpamVerbs>();
        verbs.QueryAsync("westus3", Arg.Any<CancellationToken>())
            .Returns(new RegionView("westus3", 2, IPNetwork.Parse("10.2.0.0/16"),
                IPNetwork.Parse("10.2.252.0/22"), [], new FreeSpace(0, [])));

        var (exit, stdout, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => IpamCommand.Create(sp, json),
            "ipam", "query", "-r", "westus3");

        exit.ShouldBe(CliExit.Success);
        stdout.ShouldContain("westus3");
        stdout.ShouldContain("10.2.0.0/16");

        await verbs.Received(1).QueryAsync("westus3", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ipam_query_without_region_lists_all_regions()
    {
        var verbs = Substitute.For<IIpamVerbs>();
        verbs.QueryAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<RegionView>
            {
                new("westus3", 2, IPNetwork.Parse("10.2.0.0/16"), null, [], new FreeSpace(0, [])),
            });

        var (exit, _, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => IpamCommand.Create(sp, json),
            "ipam", "query");

        exit.ShouldBe(CliExit.Success);
        await verbs.Received(1).QueryAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Inventory_emits_the_snapshot_as_json()
    {
        var verbs = Substitute.For<IInventoryVerbs>();
        verbs.GetSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(new InventorySnapshot(
                [new EnvironmentView("env1", [])],
                [new FabricItem("westus3", "sub", "rg-fab", "westus3")],
                [],
                [new SpokeItem("app1", "sub", "westus3", "rg-app1")],
                [], [], []));

        var (exit, stdout, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => InventoryCommand.Create(sp, json),
            "inventory", "--json");

        exit.ShouldBe(CliExit.Success);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("spokes")[0].GetProperty("name").GetString().ShouldBe("app1");
    }

    [Fact]
    public async Task Env_list_and_show_render_environments()
    {
        var verbs = Substitute.For<IInventoryVerbs>();
        verbs.GetEnvironmentsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<EnvironmentView> { new("env1", []) });
        verbs.GetEnvironmentAsync("env1", Arg.Any<CancellationToken>())
            .Returns(new EnvironmentView("env1", [new WorkloadItem("w1", "env1", "sub", "westus3", "rg-w1")]));

        var (listExit, listOut, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => EnvCommand.Create(sp, json),
            "env", "list");
        listExit.ShouldBe(CliExit.Success);
        listOut.ShouldContain("env1");

        var (showExit, showOut, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => EnvCommand.Create(sp, json),
            "env", "show", "env1", "--json");
        showExit.ShouldBe(CliExit.Success);
        using var doc = JsonDocument.Parse(showOut);
        doc.RootElement.GetProperty("name").GetString().ShouldBe("env1");
        doc.RootElement.GetProperty("workloads")[0].GetProperty("name").GetString().ShouldBe("w1");
    }

    [Fact]
    public async Task Env_show_on_unknown_name_is_a_clean_empty_result()
    {
        var verbs = Substitute.For<IInventoryVerbs>();
        verbs.GetEnvironmentAsync("missing", Arg.Any<CancellationToken>())
            .Returns((EnvironmentView?)null);

        var (exit, stdout, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => EnvCommand.Create(sp, json),
            "env", "show", "missing");

        exit.ShouldBe(CliExit.Success);
        stdout.ShouldContain("No environment 'missing'");
    }

    [Fact]
    public async Task Run_list_renders_the_environment_and_its_trail_as_json()
    {
        var verbs = Substitute.For<IRunVerbs>();
        var envId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        verbs.GetEnvironmentAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>())
            .Returns(new EnvironmentRecord(envId, EnvironmentKind.Spoke, "sub", "westus3", "app5", "owner",
                EnvironmentStatus.Active, "10.2.0.0/24", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        verbs.GetRunsAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>())
            .Returns(new List<RunRecord>
            {
                new(runId, envId, RunPhase.Apply, "spoke-vend.yml",
                    new Dictionary<string, string> { ["mode"] = "apply", ["spoke_cidr"] = "10.2.0.0/24" },
                    9201, "https://gh/run/9201", RunOutcome.Succeeded, null,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TrackingSource.Reconciler),
            });

        var (exit, stdout, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => RunCommand.Create(sp, json),
            "run", "list", "--env", envId.ToString(), "--json");

        exit.ShouldBe(CliExit.Success);
        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("environment").GetProperty("status").GetString().ShouldBe("Active");
        doc.RootElement.GetProperty("runs")[0].GetProperty("dispatchInputs")
            .GetProperty("spoke_cidr").GetString().ShouldBe("10.2.0.0/24");
    }

    [Fact]
    public async Task Run_show_renders_one_run_human_readable()
    {
        var verbs = Substitute.For<IRunVerbs>();
        var runId = Guid.CreateVersion7();
        verbs.GetRunAsync(runId, Arg.Any<CancellationToken>())
            .Returns(new RunRecord(runId, Guid.CreateVersion7(), RunPhase.Apply, "spoke-vend.yml",
                new Dictionary<string, string> { ["mode"] = "apply" }, 9201, "https://gh/run/9201",
                RunOutcome.Succeeded, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TrackingSource.Webhook));

        var (exit, stdout, _) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => RunCommand.Create(sp, json),
            "run", "show", runId.ToString());

        exit.ShouldBe(CliExit.Success);
        stdout.ShouldContain(runId.ToString());
        stdout.ShouldContain("Succeeded");

        await verbs.Received(1).GetRunAsync(runId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_list_with_a_bad_env_ref_exits_validation()
    {
        var verbs = Substitute.For<IRunVerbs>();

        var (exit, _, stderr) = await RunAsync(
            s => s.AddSingleton(verbs),
            (sp, json) => RunCommand.Create(sp, json),
            "run", "list", "--env", "not-a-ref");

        exit.ShouldBe(CliExit.Validation);
        stderr.ShouldContain("not a valid env ref");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        Action<IServiceCollection> configure,
        Func<IServiceProvider, Option<bool>, Command> buildCommand,
        params string[] args)
    {
        var services = new ServiceCollection();
        configure(services);
        await using var provider = services.BuildServiceProvider();

        var jsonOption = new Option<bool>("--json") { Recursive = true };
        var root = new RootCommand("pdp test root");
        root.Options.Add(jsonOption);
        root.Subcommands.Add(buildCommand(provider, jsonOption));

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
