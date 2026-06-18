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
/// The US2 destroy gate + teardown release on real Postgres (T042): <c>spoke destroy</c> refuses without
/// an explicit, matching confirmation (Article VIII / FR-007 — unbypassable, nothing dispatched); on a
/// <b>successful</b> destroy the IPAM allocation is released and becomes reusable (FR-009, completing
/// spec 004's deferred teardown); a <b>failed</b> destroy does <b>not</b> release (FR-011). GitHub is
/// faked: the dispatcher is a substitute and run completion is driven by the reconciler over WireMock.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class SpokeDestroyTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "88888888-8888-8888-8888-888888888888";

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
        _host.Dispose();
        _gitHub.Dispose();
    }

    [Fact]
    public async Task Destroy_requires_confirmation_then_releases_the_allocation_on_success()
    {
        var envId = await VendActiveSpokeAsync("app1");

        // The block was allocated at vend.
        await using (var ipam = fixture.CreateIpamContext())
        {
            (await ipam.Allocations.SingleAsync(a => a.Name == "app1")).Network.ToString().ShouldBe("10.2.0.0/24");
        }

        // A destroy without a matching confirmation is refused before any dispatch (FR-007).
        using (var scope = _host.Services.CreateScope())
        {
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();
            await Should.ThrowAsync<ConfirmationRequiredException>(() =>
                verbs.DestroyAsync(EnvRef.ById(envId), Confirmation.None));
            await Should.ThrowAsync<ConfirmationRequiredException>(() =>
                verbs.DestroyAsync(EnvRef.ById(envId), Confirmation.ForDestroy("wrong-name")));
        }

        await _dispatcher.DidNotReceive().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d => d.EnvId == envId && d.Mode == RunPhase.Destroy),
            Arg.Any<CancellationToken>());

        await using (var registry = fixture.CreateRegistryContext())
        {
            (await registry.Environments.SingleAsync(e => e.EnvId == envId)).Status.ShouldBe(EnvironmentStatus.Active);
        }

        // With the matching confirmation, the destroy dispatches (mode=destroy).
        using (var scope = _host.Services.CreateScope())
        {
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();
            var result = await verbs.DestroyAsync(EnvRef.ById(envId), Confirmation.ForDestroy("app1"));
            result.Status.ShouldBe(EnvironmentStatus.Destroying);
        }

        await WaitForDispatchAsync(envId, RunPhase.Destroy);
        await _dispatcher.Received().DispatchAsync(
            Arg.Is<WorkflowDispatch>(d =>
                d.EnvId == envId &&
                d.Mode == RunPhase.Destroy &&
                d.WorkflowFile == "spoke-destroy.yml" &&
                d.Inputs["destroy-confirm"] == "app1"),
            Arg.Any<CancellationToken>());

        // GitHub reports the destroy run succeeded → the environment is Destroyed and the block released.
        await ReconcileToTerminalAsync(envId, $"pdp destroy {envId}", "success", runId: 9002, EnvironmentStatus.Destroyed);
        await WaitForAllocationReleasedAsync("app1");

        // The released block is immediately reusable (the whole point of the teardown — FR-009).
        using (var scope = _host.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
            var reused = await ledger.AllocateAsync("westus3", "app1-again", 24);
            reused.Network.ToString().ShouldBe("10.2.0.0/24");
        }
    }

    [Fact]
    public async Task Failed_destroy_does_not_release_the_allocation()
    {
        var envId = await VendActiveSpokeAsync("app2");

        using (var scope = _host.Services.CreateScope())
        {
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();
            await verbs.DestroyAsync(EnvRef.ById(envId), Confirmation.ForDestroy("app2"));
        }

        await WaitForDispatchAsync(envId, RunPhase.Destroy);

        // The destroy run FAILS → the environment is Failed and the block is NOT released (FR-011).
        await ReconcileToTerminalAsync(envId, $"pdp destroy {envId}", "failure", runId: 9003, EnvironmentStatus.Failed);

        await using var ipam = fixture.CreateIpamContext();
        var allocation = await ipam.Allocations.SingleAsync(a => a.Name == "app2");
        allocation.Network.ToString().ShouldBe("10.2.0.0/24");
    }

    /// <summary>Vends a spoke through the direct apply path and drives it to <c>Active</c> via reconcile.</summary>
    private async Task<Guid> VendActiveSpokeAsync(string name)
    {
        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
            var verbs = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

            await ledger.RegisterRegionAsync("westus3", 2);
            var result = await verbs.CreateAsync(
                new SpokeCreateRequest(Subscription, "westus3", name, Size: 24),
                Confirmation.ForApply());
            envId = result.EnvId;
        }

        await ReconcileToTerminalAsync(envId, $"pdp apply {envId}", "success", runId: 9001, EnvironmentStatus.Active);
        return envId;
    }

    /// <summary>Stubs the run as completed, sweeps the reconciler, and waits for the environment status.</summary>
    private async Task ReconcileToTerminalAsync(
        Guid envId,
        string runName,
        string conclusion,
        long runId,
        EnvironmentStatus expected)
    {
        StubRunsList(runName, conclusion, runId);
        using (var scope = _host.Services.CreateScope())
        {
            var tracker = scope.ServiceProvider.GetRequiredService<IRunTracker>();
            await tracker.ReconcileInFlightAsync();
        }

        await WaitForStatusAsync(envId, expected);
    }

    private void StubRunsList(string runName, string conclusion, long runId)
    {
        // Reset so each phase's sweep sees only its own run (the correlation matches by run-name anyway).
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
        for (var attempt = 0; attempt < 50; attempt++)
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

    private async Task WaitForAllocationReleasedAsync(string name)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var ipam = fixture.CreateIpamContext();
            if (!await ipam.Allocations.AnyAsync(a => a.Name == name))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Allocation '{name}' was not released in time.");
    }
}
