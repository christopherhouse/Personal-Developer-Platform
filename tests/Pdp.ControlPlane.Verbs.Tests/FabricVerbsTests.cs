using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;
using Pdp.ControlPlane.Verbs;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Pdp.ControlPlane.Verbs.Tests;

/// <summary>
/// The US3 fabric verb surface on real Postgres (T048): <c>fabric create</c> registers the region in the
/// IPAM ledger and dispatches the new <c>fabric-vend.yml</c> (FR-012a); <c>fabric destroy</c> is
/// confirm-gated (Article VIII / FR-007 — unbypassable, nothing dispatched without a matching
/// confirmation) and releases <b>no</b> allocation (a fabric owns none — FR-014). GitHub is faked: the
/// dispatcher is a substitute and run completion is driven by the reconciler over WireMock.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class FabricVerbsTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Region = "eastus2";
    private const int RegionIndex = 5;

    private readonly FakeGitHubServer _gitHub = new();
    private readonly IWorkflowDispatcher _dispatcher = Substitute.For<IWorkflowDispatcher>();
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        _host = await ControlPlaneTestHost.StartAsync(
            fixture.ConnectionString,
            services =>
            {
                services.AddSingleton(_dispatcher);
                services.AddSingleton<IGitHubAppCredential>(new StubGitHubAppCredential(_gitHub.BaseUrl));
            });
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _gitHub.Dispose();
    }

    [Fact]
    public async Task Create_registers_the_region_and_dispatches_fabric_vend()
    {
        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var verbs = scope.ServiceProvider.GetRequiredService<IFabricVerbs>();
            var result = await verbs.CreateAsync(
                new FabricCreateRequest(Region, RegionIndex),
                Confirmation.ForApply());
            envId = result.EnvId;
            result.Status.ShouldBe(EnvironmentStatus.Provisioning);
            result.Outcome.ShouldBe(RunOutcome.Dispatched);
        }

        // The region was registered live in the ledger (idempotent RegisterRegionAsync) — query succeeds.
        using (var scope = _host.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
            var view = await ledger.QueryAsync(Region);
            view.RegionIndex.ShouldBe((short)RegionIndex);
        }

        await WaitForDispatchAsync(envId, RunPhase.Apply);
        await _dispatcher.Received().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d =>
                d.EnvId == envId &&
                d.Mode == RunPhase.Apply &&
                d.WorkflowFile == "fabric-vend.yml" &&
                d.Inputs["region"] == Region &&
                d.Inputs["region_index"] == RegionIndex.ToString()),
            Arg.Any<CancellationToken>());

        // The fabric environment + its dispatch run were recorded (Kind=Fabric, name=region, no CIDR).
        await using var registry = fixture.CreateRegistryContext();
        var env = await registry.Environments.SingleAsync(e => e.EnvId == envId);
        env.Kind.ShouldBe(EnvironmentKind.Fabric);
        env.Name.ShouldBe(Region);
        env.SpokeCidr.ShouldBeNull();
        var run = await registry.ProvisioningRuns.SingleAsync(r => r.EnvId == envId);
        run.WorkflowFile.ShouldBe("fabric-vend.yml");
        run.Phase.ShouldBe(RunPhase.Apply);
    }

    [Fact]
    public async Task Destroy_requires_a_matching_confirmation_before_dispatching()
    {
        var envId = await VendActiveFabricAsync();

        // No confirmation, and a mismatched one, are both refused before any dispatch (FR-007).
        using (var scope = _host.Services.CreateScope())
        {
            var verbs = scope.ServiceProvider.GetRequiredService<IFabricVerbs>();
            await Should.ThrowAsync<ConfirmationRequiredException>(() =>
                verbs.DestroyAsync(EnvRef.ById(envId), Confirmation.None));
            await Should.ThrowAsync<ConfirmationRequiredException>(() =>
                verbs.DestroyAsync(EnvRef.ById(envId), Confirmation.ForDestroy("wrong-region")));
        }

        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d => d.EnvId == envId && d.Mode == RunPhase.Destroy),
            Arg.Any<CancellationToken>());

        // With the region restated, the destroy dispatches fabric-destroy.yml (mode=destroy).
        using (var scope = _host.Services.CreateScope())
        {
            var verbs = scope.ServiceProvider.GetRequiredService<IFabricVerbs>();
            var result = await verbs.DestroyAsync(EnvRef.ById(envId), Confirmation.ForDestroy(Region));
            result.Status.ShouldBe(EnvironmentStatus.Destroying);
        }

        await WaitForDispatchAsync(envId, RunPhase.Destroy);
        await _dispatcher.Received().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d =>
                d.EnvId == envId &&
                d.Mode == RunPhase.Destroy &&
                d.WorkflowFile == "fabric-destroy.yml" &&
                d.Inputs["destroy-confirm"] == Region),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Vends a fabric through the direct apply path and drives it to <c>Active</c> via reconcile.</summary>
    private async Task<Guid> VendActiveFabricAsync()
    {
        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var verbs = scope.ServiceProvider.GetRequiredService<IFabricVerbs>();
            var result = await verbs.CreateAsync(new FabricCreateRequest(Region, RegionIndex), Confirmation.ForApply());
            envId = result.EnvId;
        }

        StubRunsList($"pdp apply {envId}", "success", runId: 7001);
        using (var scope = _host.Services.CreateScope())
        {
            var tracker = scope.ServiceProvider.GetRequiredService<IRunTracker>();
            await tracker.ReconcileInFlightAsync();
        }

        await WaitForStatusAsync(envId, EnvironmentStatus.Active);
        return envId;
    }

    private void StubRunsList(string runName, string conclusion, long runId)
    {
        _gitHub.Server.Reset();
        var json = $$"""
        {
          "total_count": 1,
          "workflow_runs": [
            {
              "id": {{runId}},
              "name": "{{runName}}",
              "status": "completed",
              "conclusion": "{{conclusion}}",
              "html_url": "https://github.com/pdp-owner/platform/actions/runs/{{runId}}"
            }
          ]
        }
        """;

        _gitHub.Server
            .Given(Request.Create().WithPath(new WildcardMatcher("*/actions/runs")).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(json));
    }

    private async Task WaitForDispatchAsync(Guid envId, RunPhase mode)
    {
        // Generous budget: the dispatch is recorded by the durable outbox asynchronously, and the
        // shared-Postgres collection runs these classes back to back, so 5s can be tight under load.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var seen = _dispatcher.ReceivedCalls()
                .Select(c => c.GetArguments()[0])
                .OfType<WorkflowDispatch>()
                .Any(d => d.EnvId == envId && d.Mode == mode);
            if (seen)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No {mode} dispatch for env {envId} in time.");
    }

    private async Task WaitForStatusAsync(Guid envId, EnvironmentStatus expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var registry = fixture.CreateRegistryContext();
            var env = await registry.Environments.SingleOrDefaultAsync(e => e.EnvId == envId);
            if (env?.Status == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Environment {envId} did not reach {expected} in time.");
    }
}
