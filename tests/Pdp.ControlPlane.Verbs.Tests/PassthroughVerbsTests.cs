using System.Net;
using NSubstitute;
using Pdp.ControlPlane.Inventory;
using Pdp.ControlPlane.Inventory.Model;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
using Pdp.ControlPlane.Verbs.Handlers;
using Shouldly;

namespace Pdp.ControlPlane.Verbs.Tests;

/// <summary>
/// US3 pass-through verbs (T049): the IPAM verbs are a thin façade over <see cref="IIpamLedger"/> and the
/// inventory/env verbs delegate to the spec-005 <see cref="IInventoryService"/> with the control plane's
/// injected credential — <b>no logic is duplicated</b> (contracts/verb-surface.md §3/§4, FR-013/FR-016).
/// These are pure delegation checks (NSubstitute) — no Postgres/Azure needed.
/// </summary>
public sealed class PassthroughVerbsTests
{
    [Fact]
    public async Task Ipam_verbs_forward_every_call_to_the_ledger()
    {
        var ledger = Substitute.For<IIpamLedger>();
        var allocation = new Allocation { Name = "app1", Network = IPNetwork.Parse("10.2.0.0/24") };
        ledger.AllocateAsync("westus3", "app1", 24, Arg.Any<CancellationToken>()).Returns(allocation);
        var region = new RegionView("westus3", 2, IPNetwork.Parse("10.2.0.0/16"), null, [], new FreeSpace(0, []));
        ledger.QueryAsync("westus3", Arg.Any<CancellationToken>()).Returns(region);
        ledger.QueryAllAsync(Arg.Any<CancellationToken>()).Returns([region]);

        var verbs = new IpamVerbs(ledger);

        (await verbs.AllocateAsync("westus3", "app1", 24)).ShouldBeSameAs(allocation);
        await verbs.ReleaseAsync("westus3", "app1");
        (await verbs.QueryAsync("westus3")).ShouldBeSameAs(region);
        (await verbs.QueryAllAsync()).Single().ShouldBeSameAs(region);

        await ledger.Received(1).AllocateAsync("westus3", "app1", 24, Arg.Any<CancellationToken>());
        await ledger.Received(1).ReleaseAsync("westus3", "app1", Arg.Any<CancellationToken>());
        await ledger.Received(1).QueryAsync("westus3", Arg.Any<CancellationToken>());
        await ledger.Received(1).QueryAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Inventory_verbs_delegate_to_the_inventory_service()
    {
        var inventory = Substitute.For<IInventoryService>();
        var snapshot = new InventorySnapshot([], [], [], [], [], [], []);
        var environments = new List<EnvironmentView> { new("env1", []) };
        var spokes = new List<SpokeItem> { new("app1", "sub", "westus3", "rg-app1") };
        var one = new EnvironmentView("env1", []);
        inventory.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(snapshot);
        inventory.GetEnvironmentsAsync(Arg.Any<CancellationToken>()).Returns(environments);
        inventory.GetSpokesAsync(Arg.Any<CancellationToken>()).Returns(spokes);
        inventory.GetEnvironmentAsync("env1", Arg.Any<CancellationToken>()).Returns(one);

        var verbs = new InventoryVerbs(inventory);

        (await verbs.GetSnapshotAsync()).ShouldBeSameAs(snapshot);
        (await verbs.GetEnvironmentsAsync()).ShouldBeSameAs(environments);
        (await verbs.GetSpokesAsync()).ShouldBeSameAs(spokes);
        (await verbs.GetEnvironmentAsync("env1")).ShouldBeSameAs(one);

        await inventory.Received(1).GetSnapshotAsync(Arg.Any<CancellationToken>());
        await inventory.Received(1).GetEnvironmentsAsync(Arg.Any<CancellationToken>());
        await inventory.Received(1).GetSpokesAsync(Arg.Any<CancellationToken>());
        await inventory.Received(1).GetEnvironmentAsync("env1", Arg.Any<CancellationToken>());
    }
}
