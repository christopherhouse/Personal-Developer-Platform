using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
using Shouldly;

namespace Pdp.ControlPlane.Ipam.Tests;

/// <summary>
/// Validates the Phase 2 checkpoint: a throwaway real Postgres comes up with the full schema
/// (btree_gist + exclusion constraints + bootstrap seed) and the reset cycle restores the
/// baseline. These exercise the harness itself, not ledger operations (US2–US4, Phase 4+).
/// </summary>
public sealed class HarnessSmokeTests(PostgresFixture fixture) : IpamIntegrationTest(fixture)
{
    [Fact]
    public async Task Migration_seeds_platform_pool_and_control_plane_reservation()
    {
        await using var ctx = Fixture.CreateContext();

        var platform = await ctx.RegionPools.SingleAsync(p => p.RegionIndex == 0);
        platform.Region.ShouldBe("platform");
        platform.Supernet.ShouldBe(IPNetwork.Parse("10.0.0.0/16"));
        platform.HubCarveout.ShouldBeNull();

        var reservation = await ctx.Allocations.SingleAsync(a => a.Name == "control-plane-vnet");
        reservation.PoolId.ShouldBe(PostgresFixture.PlatformPoolId);
        reservation.Network.ShouldBe(IPNetwork.Parse("10.0.0.0/24"));
        reservation.Kind.ShouldBe(AllocationKind.Reservation);
    }

    [Fact]
    public async Task Allocation_overlap_is_rejected_by_the_database_constraint()
    {
        var pool = await Fixture.SeedRegionAsync("eastus2", 1);

        await using var ctx = Fixture.CreateContext();
        ctx.Allocations.Add(new Allocation
        {
            Id = Guid.NewGuid(),
            PoolId = pool.Id,
            Name = "spoke-a",
            Network = IPNetwork.Parse("10.1.0.0/24"),
            PrefixLength = 24,
            Kind = AllocationKind.Spoke,
            AllocatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        // 10.1.0.0/25 sits inside 10.1.0.0/24 — the GiST exclusion constraint must refuse it.
        await using var ctx2 = Fixture.CreateContext();
        ctx2.Allocations.Add(new Allocation
        {
            Id = Guid.NewGuid(),
            PoolId = pool.Id,
            Name = "spoke-b",
            Network = IPNetwork.Parse("10.1.0.0/25"),
            PrefixLength = 25,
            Kind = AllocationKind.Spoke,
            AllocatedAt = DateTimeOffset.UtcNow,
        });

        var ex = await Should.ThrowAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        ex.InnerException.ShouldBeOfType<PostgresException>()
            .ConstraintName.ShouldBe("allocations_no_overlap");
    }

    [Fact]
    public async Task Reset_restores_the_seed_after_wiping_test_data()
    {
        // This test ran after IpamIntegrationTest reset the DB; the seed must be present and the
        // pool a prior test seeded must be gone.
        await using var ctx = Fixture.CreateContext();

        (await ctx.Allocations.CountAsync(a => a.Name == "control-plane-vnet")).ShouldBe(1);
        (await ctx.RegionPools.CountAsync()).ShouldBe(1); // only the platform pool
    }
}
