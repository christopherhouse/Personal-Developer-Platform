using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using NSubstitute;
using Pdp.ControlPlane.Inventory.Model;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.Mcp.Auth;
using Pdp.Mcp.Tools;
using Shouldly;

namespace Pdp.Mcp.Tests;

/// <summary>
/// T042 — the read MCP tools are <b>thin adapters</b> over the spec-006 read verbs (SC-003/SC-004): each
/// tool calls its verb 1:1, surfaces the structured result, and reimplements no query logic. Verbs are
/// substituted (NSubstitute) so the assertions are purely about the adapter. The suite also pins the
/// <b>division of truth</b> (FR-016): "what's deployed" routes to <see cref="IInventoryVerbs"/> (ARG), while
/// "what I asked / what happened" routes to <see cref="IRunVerbs"/> (the intent registry) — the two never
/// cross. Every read is owner-gated (EnsureOwner) before any verb runs.
/// </summary>
public sealed class ReadToolAdapterTests
{
    private const string OwnerOid = "owner-oid-0001";
    private const string Subscription = "8bd05b2f-62c5-4def-9869-f0617ebb3970";

    private static readonly IOptions<McpAuthOptions> Auth =
        Options.Create(new McpAuthOptions { OwnerOid = OwnerOid });

    private static ClaimsPrincipal Caller(string oid) =>
        new(new ClaimsIdentity([new Claim("oid", oid)], "test"));

    private static ClaimsPrincipal Owner => Caller(OwnerOid);

    private static RegionView SomeRegion(string region) =>
        new(region, 7, IPNetwork.Parse("10.7.0.0/16"), null, [], new FreeSpace(0, []));

    private static InventorySnapshot EmptySnapshot() => new([], [], [], [], [], [], []);

    // --- IPAM (QueryIpam — one region or all) -----------------------------------------------------------

    [Fact]
    public async Task QueryIpam_with_a_region_calls_QueryAsync_once()
    {
        var ipam = Substitute.For<IIpamVerbs>();
        ipam.QueryAsync("westus3", Arg.Any<CancellationToken>()).Returns(SomeRegion("westus3"));
        var tools = new IpamTools(ipam, Auth);

        var result = await tools.QueryIpam(Owner, "westus3");

        result.ShouldHaveSingleItem().Region.ShouldBe("westus3");
        await ipam.Received(1).QueryAsync("westus3", Arg.Any<CancellationToken>());
        await ipam.DidNotReceive().QueryAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryIpam_without_a_region_calls_QueryAllAsync_once()
    {
        var ipam = Substitute.For<IIpamVerbs>();
        ipam.QueryAllAsync(Arg.Any<CancellationToken>()).Returns([SomeRegion("westus3"), SomeRegion("eastus2")]);
        var tools = new IpamTools(ipam, Auth);

        var result = await tools.QueryIpam(Owner);

        result.Count.ShouldBe(2);
        await ipam.Received(1).QueryAllAsync(Arg.Any<CancellationToken>());
        await ipam.DidNotReceive().QueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- Inventory (ARG side of the division of truth) --------------------------------------------------

    [Fact]
    public async Task WhatsDeployed_calls_the_inventory_snapshot_verb_once_over_ARG()
    {
        var inventory = Substitute.For<IInventoryVerbs>();
        var snapshot = EmptySnapshot();
        inventory.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        var tools = new InventoryTools(inventory, Auth);

        var result = await tools.WhatsDeployed(Owner);

        result.ShouldBeSameAs(snapshot);
        await inventory.Received(1).GetSnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListEnvironments_calls_the_inventory_environments_verb_once_over_ARG()
    {
        var inventory = Substitute.For<IInventoryVerbs>();
        inventory.GetEnvironmentsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var tools = new InventoryTools(inventory, Auth);

        await tools.ListEnvironments(Owner);

        await inventory.Received(1).GetEnvironmentsAsync(Arg.Any<CancellationToken>());
    }

    // --- Run / registry (intent + history side of the division of truth) -------------------------------

    [Fact]
    public async Task ShowEnvironment_parses_the_env_ref_and_reads_recorded_intent_from_the_registry()
    {
        var runs = Substitute.For<IRunVerbs>();
        var tools = new RunTools(runs, Auth);

        await tools.ShowEnvironment($"spoke:{Subscription}:app5", Owner);

        await runs.Received(1).GetEnvironmentAsync(
            Arg.Is<EnvRef>(e => e.Kind == EnvironmentKind.Spoke && e.Subscription == Subscription && e.Name == "app5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunHistory_reads_the_provisioning_run_trail_from_the_registry()
    {
        var runs = Substitute.For<IRunVerbs>();
        runs.GetRunsAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>()).Returns([]);
        var tools = new RunTools(runs, Auth);

        await tools.RunHistory($"spoke:{Subscription}:app5", Owner);

        await runs.Received(1).GetRunsAsync(
            Arg.Is<EnvRef>(e => e.Kind == EnvironmentKind.Spoke && e.Name == "app5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunStatus_reads_a_single_run_by_id_from_the_registry()
    {
        var runs = Substitute.For<IRunVerbs>();
        var runId = Guid.CreateVersion7();
        var tools = new RunTools(runs, Auth);

        await tools.RunStatus(runId.ToString(), Owner);

        await runs.Received(1).GetRunAsync(runId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ShowEnvironment_with_an_unparseable_ref_throws_before_the_verb_runs()
    {
        var runs = Substitute.For<IRunVerbs>();
        var tools = new RunTools(runs, Auth);

        await Should.ThrowAsync<McpException>(() => tools.ShowEnvironment("not-an-env-ref", Owner));
        await runs.DidNotReceive().GetEnvironmentAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunStatus_with_a_non_guid_id_throws_before_the_verb_runs()
    {
        var runs = Substitute.For<IRunVerbs>();
        var tools = new RunTools(runs, Auth);

        await Should.ThrowAsync<McpException>(() => tools.RunStatus("not-a-guid", Owner));
        await runs.DidNotReceive().GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // --- Owner gate (defense in depth — every read tool) ------------------------------------------------

    [Fact]
    public async Task A_non_owner_is_refused_by_every_read_tool_before_any_verb_runs()
    {
        var ipam = Substitute.For<IIpamVerbs>();
        var inventory = Substitute.For<IInventoryVerbs>();
        var runs = Substitute.For<IRunVerbs>();
        var someoneElse = Caller("someone-else");

        await Should.ThrowAsync<McpException>(() => new IpamTools(ipam, Auth).QueryIpam(someoneElse));
        await Should.ThrowAsync<McpException>(() => new InventoryTools(inventory, Auth).WhatsDeployed(someoneElse));
        await Should.ThrowAsync<McpException>(() => new InventoryTools(inventory, Auth).ListEnvironments(someoneElse));
        await Should.ThrowAsync<McpException>(() => new RunTools(runs, Auth).ShowEnvironment($"spoke:{Subscription}:app5", someoneElse));

        await ipam.DidNotReceive().QueryAllAsync(Arg.Any<CancellationToken>());
        await inventory.DidNotReceive().GetSnapshotAsync(Arg.Any<CancellationToken>());
        await inventory.DidNotReceive().GetEnvironmentsAsync(Arg.Any<CancellationToken>());
        await runs.DidNotReceive().GetEnvironmentAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>());
    }
}
