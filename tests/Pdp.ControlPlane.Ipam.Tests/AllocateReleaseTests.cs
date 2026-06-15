using System.Net;
using Microsoft.EntityFrameworkCore;
using Pdp.ControlPlane.Ipam;
using Shouldly;

namespace Pdp.ControlPlane.Ipam.Tests;

/// <summary>
/// US2 / quickstart Scenario 4 (SC-003, SC-004): allocate returns a valid block committed
/// before return; repeat-by-name is idempotent; a name reused for a different size is rejected;
/// allocating against an unregistered region is refused (FR-012); release reclaims space and is
/// a clean no-op when unknown; and a reservation cannot be released (contract).
/// </summary>
public sealed class AllocateReleaseTests(PostgresFixture fixture) : IpamIntegrationTest(fixture)
{
    [Fact]
    public async Task Allocate_returns_a_durable_block_below_the_carve_out()
    {
        await Fixture.SeedRegionAsync("eastus2", 1);

        await using (var ctx = Fixture.CreateContext())
        {
            var allocation = await new Ledger(ctx).AllocateAsync("eastus2", "spoke-a");
            allocation.Network.ShouldBe(IPNetwork.Parse("10.1.0.0/24"));
            allocation.PrefixLength.ShouldBe((short)24);
            allocation.Kind.ShouldBe(Entities.AllocationKind.Spoke);
        }

        // Durable: a fresh context (new connection) sees the committed row.
        await using var verify = Fixture.CreateContext();
        var persisted = await verify.Allocations.SingleAsync(a => a.Name == "spoke-a");
        persisted.Network.ShouldBe(IPNetwork.Parse("10.1.0.0/24"));
    }

    [Fact]
    public async Task Repeat_allocate_with_the_same_name_returns_the_same_block()
    {
        await Fixture.SeedRegionAsync("eastus2", 1);

        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);

        var first = await ledger.AllocateAsync("eastus2", "spoke-a");
        var retry = await ledger.AllocateAsync("eastus2", "spoke-a");

        retry.Network.ShouldBe(first.Network);
        retry.Id.ShouldBe(first.Id);

        // Idempotent: the retry did not consume a second block.
        await using var verify = Fixture.CreateContext();
        (await verify.Allocations.CountAsync(a => a.Kind == Entities.AllocationKind.Spoke)).ShouldBe(1);
    }

    [Fact]
    public async Task Reusing_a_live_name_for_a_different_size_is_rejected()
    {
        await Fixture.SeedRegionAsync("eastus2", 1);

        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);
        await ledger.AllocateAsync("eastus2", "spoke-a", prefixLength: 24);

        await Should.ThrowAsync<AllocationNameConflictException>(
            () => ledger.AllocateAsync("eastus2", "spoke-a", prefixLength: 25));
    }

    [Fact]
    public async Task Allocate_against_an_unregistered_region_is_refused()
    {
        await using var ctx = Fixture.CreateContext();

        await Should.ThrowAsync<RegionNotRegisteredException>(
            () => new Ledger(ctx).AllocateAsync("westus3", "spoke-a"));
    }

    [Fact]
    public async Task Allocate_below_the_permitted_prefix_range_is_refused()
    {
        await Fixture.SeedRegionAsync("eastus2", 1);

        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);

        // /30 is smaller than the permitted /29 floor; /21 is larger than the /22 ceiling.
        await Should.ThrowAsync<InvalidPrefixLengthException>(
            () => ledger.AllocateAsync("eastus2", "too-small", prefixLength: 30));
        await Should.ThrowAsync<InvalidPrefixLengthException>(
            () => ledger.AllocateAsync("eastus2", "too-big", prefixLength: 21));
    }

    [Fact]
    public async Task Release_reclaims_the_space_for_a_future_allocation()
    {
        await Fixture.SeedRegionAsync("eastus2", 1);

        await using var ctx = Fixture.CreateContext();
        var ledger = new Ledger(ctx);

        var first = await ledger.AllocateAsync("eastus2", "spoke-a");
        first.Network.ShouldBe(IPNetwork.Parse("10.1.0.0/24"));

        await ledger.ReleaseAsync("eastus2", "spoke-a");

        // The freed block is the lowest aligned free block again, so first-fit reuses it.
        var reused = await ledger.AllocateAsync("eastus2", "spoke-b");
        reused.Network.ShouldBe(IPNetwork.Parse("10.1.0.0/24"));

        await using var verify = Fixture.CreateContext();
        (await verify.Allocations.AnyAsync(a => a.Name == "spoke-a")).ShouldBeFalse();
    }

    [Fact]
    public async Task Release_of_an_unknown_name_is_a_clean_no_op()
    {
        await Fixture.SeedRegionAsync("eastus2", 1);

        await using var ctx = Fixture.CreateContext();
        // Never allocated — releasing it must neither throw nor double-free.
        await Should.NotThrowAsync(() => new Ledger(ctx).ReleaseAsync("eastus2", "never-existed"));
    }

    [Fact]
    public async Task Release_of_a_reservation_is_refused()
    {
        // The seeded control-plane-vnet reservation lives in the platform pool (index 0).
        await using var ctx = Fixture.CreateContext();

        await Should.ThrowAsync<CannotReleaseReservationException>(
            () => new Ledger(ctx).ReleaseAsync("platform", IpamSeedData.ControlPlaneVnetName));

        // It is still there afterwards.
        await using var verify = Fixture.CreateContext();
        (await verify.Allocations.CountAsync(a => a.Name == IpamSeedData.ControlPlaneVnetName)).ShouldBe(1);
    }
}
