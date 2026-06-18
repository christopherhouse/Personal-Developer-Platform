using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;

namespace Pdp.ControlPlane.Verbs.Tests;

/// <summary>
/// The US1 vend spine on real Postgres (T025): <c>spoke create</c> allocates a block by size from the
/// ledger (Gate-G1), and the lifecycle saga records the <c>environments</c> row + a <c>provisioning_runs</c>
/// row <b>atomically</b> in one durable transaction, then enqueues the apply dispatch through the outbox.
/// GitHub is faked: the dispatcher is a no-op so the assertions focus on the allocate+record atomicity
/// (the dispatch wiring itself is covered by the Dispatch tests).
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class SpokeCreateTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "22222222-2222-2222-2222-222222222222";

    private readonly IWorkflowDispatcher _dispatcher = Substitute.For<IWorkflowDispatcher>();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _host = await ControlPlaneTestHost.StartAsync(
            fixture.ConnectionString,
            services => services.AddSingleton(_dispatcher));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Create_allocates_by_size_and_records_env_and_run_atomically()
    {
        using var scope = _host.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
        var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

        await ledger.RegisterRegionAsync("westus3", 2);

        var result = await verbs.CreateAsync(
            new SpokeCreateRequest(Subscription, "westus3", "app1", Size: 24),
            Confirmation.ForApply());

        result.Status.ShouldBe(EnvironmentStatus.Provisioning);
        result.Outcome.ShouldBe(RunOutcome.Dispatched);
        result.RunId.ShouldNotBeNull();

        // The block is the lowest free aligned /24 in the westus3 (/16 index 2) pool.
        await using var ipam = fixture.CreateIpamContext();
        var allocation = await ipam.Allocations.SingleAsync(a => a.Name == "app1");
        allocation.Network.ToString().ShouldBe("10.2.0.0/24");

        await using var registry = fixture.CreateRegistryContext();
        var environment = await registry.Environments.SingleAsync(e => e.Name == "app1");
        environment.EnvId.ShouldBe(result.EnvId);
        environment.Status.ShouldBe(EnvironmentStatus.Provisioning);
        environment.Kind.ShouldBe(EnvironmentKind.Spoke);
        environment.SpokeCidr.ShouldNotBeNull();
        environment.SpokeCidr!.Value.ToString().ShouldBe("10.2.0.0/24");

        var run = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == result.EnvId);
        run.RunId.ShouldBe(result.RunId!.Value);
        run.Phase.ShouldBe(RunPhase.Apply);
        run.WorkflowFile.ShouldBe("spoke-vend.yml");
        run.Outcome.ShouldBe(RunOutcome.Dispatched);

        // The exact dispatched inputs are captured as queryable jsonb (asserted via the parsed doc, so
        // the test is independent of Postgres's jsonb whitespace normalization).
        var inputs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(run.DispatchInputs)!;
        inputs["spoke_cidr"].ShouldBe("10.2.0.0/24");
        inputs["mode"].ShouldBe("apply");
        inputs["env_id"].ShouldBe(result.EnvId.ToString());
        inputs["region"].ShouldBe("westus3");
        inputs["target_subscription_id"].ShouldBe(Subscription);
    }

    [Fact]
    public async Task Create_dispatches_the_apply_workflow_through_the_outbox()
    {
        using var scope = _host.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
        var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

        await ledger.RegisterRegionAsync("westus3", 2);
        var result = await verbs.CreateAsync(
            new SpokeCreateRequest(Subscription, "westus3", "app2", Size: 24),
            Confirmation.ForApply());

        // The cascaded dispatch is durable (processed after the start transaction commits); give the
        // outbox a moment, then assert the apply dispatch reached the (faked) execution-plane boundary.
        await WaitForDispatchAsync(result.EnvId);

        await _dispatcher.Received().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d =>
                d.WorkflowFile == "spoke-vend.yml" &&
                d.EnvId == result.EnvId &&
                d.Mode == RunPhase.Apply &&
                d.Inputs["spoke_cidr"] == "10.2.0.0/24"),
            Arg.Any<CancellationToken>());
    }

    private async Task WaitForDispatchAsync(Guid envId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var calls = _dispatcher.ReceivedCalls()
                .Select(c => c.GetArguments()[0])
                .OfType<WorkflowDispatch>()
                .Any(d => d.EnvId == envId);
            if (calls)
            {
                return;
            }

            await Task.Delay(100);
        }
    }
}
