using System.Net;
using Microsoft.EntityFrameworkCore;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
using Shouldly;

namespace Pdp.ControlPlane.Ipam.Tests;

/// <summary>
/// US3 / quickstart Scenario 5: <c>register_region</c> derives the region's <c>/16</c> supernet
/// and reserves its hub <c>/22</c> carve-out in one step; spokes allocate only from the
/// remainder below the carve-out and never receive it; and a second region whose supernet
/// overlaps an existing one is refused by the database (FR-011). Idempotent re-register is a
/// no-op success; a region re-registered under a different index, or an index outside 1–255, is
/// rejected (contracts/ipam-operations.md).
/// </summary>
public sealed class RegionRegistrationTests(PostgresFixture fixture) : IpamIntegrationTest(fixture)
{
    [Fact]
    public async Task Register_derives_the_supernet_and_reserves_the_hub_carveout()
    {
        await using (var ctx = Fixture.CreateContext())
        {
            var pool = await new Ledger(ctx).RegisterRegionAsync("eastus2", 5);
            pool.Region.ShouldBe("eastus2");
            pool.RegionIndex.ShouldBe((short)5);
            pool.Supernet.ShouldBe(IPNetwork.Parse("10.5.0.0/16"));
            pool.HubCarveout.ShouldBe(IPNetwork.Parse("10.5.252.0/22"));
        }

        // Durable: a fresh context (new connection) sees the committed pool with its carve-out.
        await using var verify = Fixture.CreateContext();
        var persisted = await verify.RegionPools.SingleAsync(p => p.Region == "eastus2");
        persisted.Supernet.ShouldBe(IPNetwork.Parse("10.5.0.0/16"));
        persisted.HubCarveout.ShouldBe(IPNetwork.Parse("10.5.252.0/22"));
    }

    [Fact]
    public async Task Spokes_allocate_only_from_below_the_carveout_and_never_receive_it()
    {
        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);
        await ledger.RegisterRegionAsync("eastus2", 5);

        var carveout = IPNetwork.Parse("10.5.252.0/22");

        // Drain every allocatable /22 in the region. A /16 holds 64 /22 blocks; the top one is the
        // reserved carve-out, so exactly 63 are allocatable — and none of them is the carve-out.
        var allocated = new List<IPNetwork>();
        for (var i = 0; i < 64; i++)
        {
            try
            {
                var block = await ledger.AllocateAsync("eastus2", $"spoke-{i}", prefixLength: 22);
                allocated.Add(block.Network);
            }
            catch (PoolExhaustedException)
            {
                break;
            }
        }

        allocated.Count.ShouldBe(63);
        allocated.ShouldBeUnique();
        allocated.ShouldNotContain(carveout);
        allocated.ShouldAllBe(block => !Overlaps(block, carveout));

        // The 64th /22 would be the carve-out — the pool is exhausted instead.
        await Should.ThrowAsync<PoolExhaustedException>(
            () => ledger.AllocateAsync("eastus2", "one-too-many", prefixLength: 22));
    }

    [Fact]
    public async Task Re_registering_the_same_region_with_the_same_index_is_an_idempotent_no_op()
    {
        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);

        var first = await ledger.RegisterRegionAsync("eastus2", 5);
        var again = await ledger.RegisterRegionAsync("eastus2", 5);

        again.Id.ShouldBe(first.Id);

        // No duplicate pool was created.
        await using var verify = Fixture.CreateContext();
        (await verify.RegionPools.CountAsync(p => p.Region == "eastus2")).ShouldBe(1);
    }

    [Fact]
    public async Task Re_registering_a_region_under_a_different_index_is_rejected()
    {
        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);
        await ledger.RegisterRegionAsync("eastus2", 5);

        await Should.ThrowAsync<RegionAlreadyExistsException>(
            () => ledger.RegisterRegionAsync("eastus2", 6));
    }

    [Fact]
    public async Task Registering_a_second_region_with_an_overlapping_supernet_is_refused()
    {
        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);
        await ledger.RegisterRegionAsync("eastus2", 5);

        // A different region name re-using index 5 maps to the same 10.5.0.0/16 supernet — the
        // database exclusion constraint refuses it (FR-011).
        await Should.ThrowAsync<SupernetOverlapException>(
            () => ledger.RegisterRegionAsync("westus3", 5));

        await using var verify = Fixture.CreateContext();
        (await verify.RegionPools.CountAsync(p => p.RegionIndex == 5)).ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]   // reserved for the platform-shared pool
    [InlineData(-1)]
    [InlineData(256)] // beyond the second octet
    public async Task Registering_an_out_of_range_index_is_rejected(int index)
    {
        await using var ctx = Fixture.CreateContext();

        await Should.ThrowAsync<InvalidRegionIndexException>(
            () => new Ledger(ctx).RegisterRegionAsync("badregion", index));
    }

    // Two networks overlap iff one contains the other's base address. The blocks here are /22 and
    // the carve-out is an aligned /22, so this reduces to range containment.
    private static bool Overlaps(IPNetwork a, IPNetwork b)
        => a.Contains(b.BaseAddress) || b.Contains(a.BaseAddress);
}
