using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
using Shouldly;

namespace Pdp.ControlPlane.Ipam.Tests;

/// <summary>
/// US2 / quickstart Scenario 2 (SC-002): after the ledger has handed out blocks, the database
/// itself refuses any overlapping range. The non-overlap guarantee lives in the
/// <c>allocations_no_overlap</c> GiST exclusion constraint (FR-006), not in application code —
/// so this drives a deliberate overlapping insert straight at the table and asserts the engine
/// rejects it.
/// </summary>
public sealed class NonOverlapTests(PostgresFixture fixture) : IpamIntegrationTest(fixture)
{
    [Fact]
    public async Task Database_rejects_a_block_overlapping_a_ledger_allocation()
    {
        var pool = await Fixture.SeedRegionAsync("eastus2", 1);

        // Hand out a real block through the ledger so the conflicting row is a genuine allocation.
        await using (var ledgerCtx = Fixture.CreateContext())
        {
            var allocated = await new Ledger(ledgerCtx).AllocateAsync("eastus2", "spoke-a");
            allocated.Network.ShouldBe(IPNetwork.Parse("10.1.0.0/24"));
        }

        // 10.1.0.128/25 sits inside the allocated 10.1.0.0/24 — the exclusion constraint must
        // refuse it even though it carries a different, unused name.
        await using var ctx = Fixture.CreateContext();
        ctx.Allocations.Add(new Allocation
        {
            Id = Guid.NewGuid(),
            PoolId = pool.Id,
            Name = "overlapping-intruder",
            Network = IPNetwork.Parse("10.1.0.128/25"),
            PrefixLength = 25,
            Kind = AllocationKind.Spoke,
            AllocatedAt = DateTimeOffset.UtcNow,
        });

        var ex = await Should.ThrowAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        ex.InnerException.ShouldBeOfType<PostgresException>()
            .ConstraintName.ShouldBe("allocations_no_overlap");

        // Nothing overlapping was persisted: only the original allocation survives.
        await using var verifyCtx = Fixture.CreateContext();
        (await verifyCtx.Allocations.CountAsync(a => a.PoolId == pool.Id)).ShouldBe(1);
    }
}
