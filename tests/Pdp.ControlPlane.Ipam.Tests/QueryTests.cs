using System.Net;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
using Shouldly;

namespace Pdp.ControlPlane.Ipam.Tests;

/// <summary>
/// US4 / quickstart Scenario 6 (SC-005): <c>query</c> reports a region's supernet, reserved hub
/// carve-out, every live allocation (name/size/kind), and the remaining free space; a released
/// block no longer appears as allocated. <c>query_all</c> reports every registered region —
/// including the seeded platform pool and its <c>control-plane-vnet</c> reservation — and
/// querying an unregistered region is refused (FR-012).
/// </summary>
public sealed class QueryTests(PostgresFixture fixture) : IpamIntegrationTest(fixture)
{
    [Fact]
    public async Task Query_reports_the_supernet_carveout_live_allocations_and_free_space()
    {
        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);
        await ledger.RegisterRegionAsync("eastus2", 1);
        await ledger.AllocateAsync("eastus2", "spoke-a"); // /24
        await ledger.AllocateAsync("eastus2", "spoke-b"); // /24

        var view = await ledger.QueryAsync("eastus2");

        view.Region.ShouldBe("eastus2");
        view.RegionIndex.ShouldBe((short)1);
        view.Supernet.ShouldBe(IPNetwork.Parse("10.1.0.0/16"));
        view.HubCarveout.ShouldBe(IPNetwork.Parse("10.1.252.0/22"));

        view.Allocations.Select(a => a.Name).ShouldBe(["spoke-a", "spoke-b"], ignoreOrder: true);
        view.Allocations.ShouldAllBe(a => a.Kind == AllocationKind.Spoke);
        view.Allocations.ShouldAllBe(a => a.PrefixLength == 24);

        // Free = /16 (65536) − the /22 carve-out (1024) − two /24 spokes (512) = 64000 addresses.
        view.Free.AddressCount.ShouldBe(64000UL);

        // The reported free blocks are the exact complement: each is a real CIDR block inside the
        // supernet, clear of the carve-out and every allocation, and together they sum to the count.
        var carveout = IPNetwork.Parse("10.1.252.0/22");
        view.Free.Blocks.ShouldNotBeEmpty();
        view.Free.Blocks
            .Aggregate(0UL, (sum, b) => sum + (1UL << (32 - b.PrefixLength)))
            .ShouldBe(64000UL);
        view.Free.Blocks.ShouldAllBe(b => view.Supernet.Contains(b.BaseAddress));
        view.Free.Blocks.ShouldAllBe(b => !Overlaps(b, carveout));
    }

    [Fact]
    public async Task A_released_block_no_longer_appears_in_the_query()
    {
        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);
        await ledger.RegisterRegionAsync("eastus2", 1);
        await ledger.AllocateAsync("eastus2", "spoke-a");
        await ledger.AllocateAsync("eastus2", "spoke-b");

        var before = await ledger.QueryAsync("eastus2");
        before.Allocations.Select(a => a.Name).ShouldContain("spoke-a");
        var freeBefore = before.Free.AddressCount;

        await ledger.ReleaseAsync("eastus2", "spoke-a");

        var after = await ledger.QueryAsync("eastus2");
        after.Allocations.Select(a => a.Name).ShouldNotContain("spoke-a");
        after.Allocations.Select(a => a.Name).ShouldContain("spoke-b");
        // The freed /24 (256 addresses) returns to the free pool.
        after.Free.AddressCount.ShouldBe(freeBefore + 256UL);
    }

    [Fact]
    public async Task Query_all_includes_the_platform_pool_and_its_control_plane_reservation()
    {
        await using var ctx = Fixture.CreateContext();
        var views = await new Ledger(ctx).QueryAllAsync();

        // The reset baseline is the freshly-migrated DB: only the seeded platform pool exists.
        var platform = views.ShouldHaveSingleItem();
        platform.Region.ShouldBe("platform");
        platform.RegionIndex.ShouldBe((short)0);
        platform.Supernet.ShouldBe(IPNetwork.Parse("10.0.0.0/16"));
        platform.HubCarveout.ShouldBeNull();

        var reservation = platform.Allocations.ShouldHaveSingleItem();
        reservation.Name.ShouldBe(IpamSeedData.ControlPlaneVnetName);
        reservation.Kind.ShouldBe(AllocationKind.Reservation);
        reservation.Network.ShouldBe(IPNetwork.Parse(IpamSeedData.ControlPlaneVnetCidr));

        // Free = /16 (65536) − the /24 reservation (256) = 65280; the platform pool has no carve-out.
        platform.Free.AddressCount.ShouldBe(65280UL);
    }

    [Fact]
    public async Task Query_all_reports_every_registered_region_in_index_order()
    {
        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);
        await ledger.RegisterRegionAsync("eastus2", 1);
        await ledger.RegisterRegionAsync("westus3", 3);

        var views = await ledger.QueryAllAsync();

        // Platform (index 0) then the two geographic regions, ascending by index.
        views.Select(v => v.Region).ShouldBe(["platform", "eastus2", "westus3"]);
    }

    [Fact]
    public async Task Query_of_an_unregistered_region_is_refused()
    {
        await using var ctx = Fixture.CreateContext();

        await Should.ThrowAsync<RegionNotRegisteredException>(
            () => new Ledger(ctx).QueryAsync("westus3"));
    }

    // Two CIDR blocks overlap iff one contains the other's base address (both are aligned).
    private static bool Overlaps(IPNetwork a, IPNetwork b)
        => a.Contains(b.BaseAddress) || b.Contains(a.BaseAddress);
}
