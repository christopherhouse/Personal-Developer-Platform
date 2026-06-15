using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pdp.ControlPlane.Ipam;
using Shouldly;

namespace Pdp.ControlPlane.Ipam.Tests;

/// <summary>
/// US2 / quickstart Scenario 3 (SC-001): firing ≥100 concurrent <c>allocate</c> calls at one
/// pool yields distinct, non-overlapping blocks (or clean <see cref="PoolExhaustedException"/>),
/// never a double-allocation and never a corrupted ledger. The per-pool
/// <c>pg_advisory_xact_lock</c> serializes the callers; the GiST exclusion constraint is the
/// backstop (FR-008). Each task gets its own context/connection — the real concurrent shape.
/// </summary>
public sealed class ConcurrencyTests(PostgresFixture fixture) : IpamIntegrationTest(fixture)
{
    private const int RequestCount = 100;

    [Fact]
    public async Task Concurrent_allocations_on_one_pool_never_overlap_or_double_allocate()
    {
        await Fixture.SeedRegionAsync("eastus2", 1);

        // Bound the client-side connection pool so 100 in-flight tasks (most parked on the
        // advisory lock) stay well under the container's max_connections while still racing.
        var connectionString = new NpgsqlConnectionStringBuilder(Fixture.ConnectionString)
        {
            MaxPoolSize = 20,
        }.ConnectionString;

        var allocate = Enumerable.Range(0, RequestCount).Select(async i =>
        {
            var options = new DbContextOptionsBuilder<IpamDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            await using var ctx = new IpamDbContext(options);
            return await new Ledger(ctx).AllocateAsync("eastus2", $"spoke-{i}");
        });

        var blocks = await Task.WhenAll(allocate);

        // All 100 fit below the /22 carve-out, so every call succeeds with a distinct /24.
        blocks.Length.ShouldBe(RequestCount);
        blocks.Select(b => b.Network.ToString()).Distinct().Count().ShouldBe(RequestCount);
        AssertPairwiseDisjoint(blocks.Select(b => b.Network));

        // And the ledger itself holds exactly those rows — no double-allocation slipped through.
        await using var verify = Fixture.CreateContext();
        var persisted = await verify.Allocations
            .Where(a => a.Kind == Entities.AllocationKind.Spoke)
            .Select(a => a.Network)
            .ToListAsync();
        persisted.Count.ShouldBe(RequestCount);
        AssertPairwiseDisjoint(persisted);
    }

    private static void AssertPairwiseDisjoint(IEnumerable<IPNetwork> networks)
    {
        var ranges = networks
            .Select(n => (Start: ToUInt(n.BaseAddress), End: ToUInt(n.BaseAddress) + (1u << (32 - n.PrefixLength))))
            .OrderBy(r => r.Start)
            .ToList();

        for (var i = 1; i < ranges.Count; i++)
        {
            ranges[i].Start.ShouldBeGreaterThanOrEqualTo(
                ranges[i - 1].End,
                $"block starting at {ranges[i].Start} overlaps the previous block");
        }
    }

    private static uint ToUInt(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
