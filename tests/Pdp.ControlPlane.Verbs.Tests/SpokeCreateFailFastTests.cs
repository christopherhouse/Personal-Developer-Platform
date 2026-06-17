using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.TestSupport;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;

namespace Pdp.ControlPlane.Verbs.Tests;

/// <summary>
/// US1 fail-fast guarantees on real Postgres (T028): an invalid region (no fabric) is refused before
/// any write, and a failure during allocation drops the claim — leaving <b>no orphan environment row</b>
/// and <b>no leaked allocation</b> (FR-023/FR-011).
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class SpokeCreateFailFastTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "33333333-3333-3333-3333-333333333333";

    private readonly IWorkflowDispatcher _dispatcher = Substitute.For<IWorkflowDispatcher>();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _host = await ControlPlaneTestHost.StartAsync(
            fixture.ConnectionString,
            services => services.AddSingleton(_dispatcher));
    }

    public async Task DisposeAsync() => await _host.StopAsync();

    [Fact]
    public async Task Create_against_an_unregistered_region_fails_before_any_write()
    {
        using var scope = _host.Services.CreateScope();
        var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

        await Should.ThrowAsync<RegionNotRegisteredException>(() => verbs.CreateAsync(
            new SpokeCreateRequest(Subscription, "eastus2", "app1", Size: 24),
            Confirmation.ForApply()));

        // No fabric → no intent row, no run, no allocation, and nothing dispatched.
        await using var registry = fixture.CreateRegistryContext();
        (await registry.Environments.AnyAsync(e => e.Name == "app1")).ShouldBeFalse();
        (await registry.ProvisioningRuns.AnyAsync()).ShouldBeFalse();

        await using var ipam = fixture.CreateIpamContext();
        (await ipam.Allocations.AnyAsync(a => a.Name == "app1")).ShouldBeFalse();

        await _dispatcher.DidNotReceive().DispatchAsync(Arg.Any<WorkflowDispatch>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Allocation_failure_after_claim_leaves_no_orphan_row_and_no_leak()
    {
        using var scope = _host.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
        var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

        await ledger.RegisterRegionAsync("westus3", 2);
        // Pre-existing /24 under this name; a create requesting a different size collides at allocate.
        await ledger.AllocateAsync("westus3", "app9", 24);

        await Should.ThrowAsync<AllocationNameConflictException>(() => verbs.CreateAsync(
            new SpokeCreateRequest(Subscription, "westus3", "app9", Size: 25),
            Confirmation.ForApply()));

        // The claim was rolled back — no orphan environment row remains (FR-023).
        await using var registry = fixture.CreateRegistryContext();
        (await registry.Environments.AnyAsync(e => e.Name == "app9")).ShouldBeFalse();

        // The pre-existing allocation is untouched and no second block leaked (FR-011).
        await using var ipam = fixture.CreateIpamContext();
        var allocations = await ipam.Allocations.Where(a => a.Name == "app9").ToListAsync();
        allocations.Count.ShouldBe(1);
        allocations[0].Network.ToString().ShouldBe("10.2.0.0/24");
    }
}
