using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Shouldly;

namespace Pdp.ControlPlane.Verbs.Tests;

/// <summary>
/// Issue #48 — an environment whose apply run never records terminal stays <c>Provisioning</c> forever, and
/// the single-flight guard (FR-022a) then rejects <b>every</b> mutating verb (including destroy), so it can
/// never be torn down through the sanctioned path. The maintenance reset verb is the in-product recovery:
/// an Article VIII-gated, owner-only force-terminal that clears the guard (registry status only — no
/// dispatch, no Azure mutation, no IPAM release), after which the normal destroy flow proceeds. Runs on real
/// Postgres; the dispatcher is substituted so a create leaves the environment wedged in <c>Provisioning</c>.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class EnvironmentResetTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "77777777-7777-7777-7777-777777777777";

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
    public async Task A_wedged_spoke_is_undestroyable_until_reset_then_can_be_torn_down()
    {
        var envId = await WedgedSpokeAsync("stuck");

        // The wedge: with the apply stuck non-terminal, plan-destroy is rejected by the single-flight guard.
        using (var scope = _host.Services.CreateScope())
        {
            var spoke = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();
            await Should.ThrowAsync<OperationInProgressException>(() =>
                spoke.PlanDestroyAsync(EnvRef.ById(envId)));
        }

        // Reset without a matching confirmation is refused before any mutation (Article VIII / FR-007).
        using (var scope = _host.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IEnvironmentMaintenanceVerbs>();
            var preview = await maintenance.PlanResetAsync(EnvRef.ById(envId));
            preview.CurrentStatus.ShouldBe(EnvironmentStatus.Provisioning);
            preview.Name.ShouldBe("stuck");

            await Should.ThrowAsync<ConfirmationRequiredException>(() =>
                maintenance.ResetAsync(EnvRef.ById(envId), Confirmation.None));
            await Should.ThrowAsync<ConfirmationRequiredException>(() =>
                maintenance.ResetAsync(EnvRef.ById(envId), Confirmation.ForDestroy("wrong")));
        }

        // Still wedged — a rejected reset changed nothing.
        await using (var registry = fixture.CreateRegistryContext())
        {
            (await registry.Environments.SingleAsync(e => e.EnvId == envId)).Status.ShouldBe(EnvironmentStatus.Provisioning);
        }

        // With the matching restatement, the reset forces the environment terminal (Failed) — registry only.
        using (var scope = _host.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IEnvironmentMaintenanceVerbs>();
            var result = await maintenance.ResetAsync(EnvRef.ById(envId), Confirmation.ForDestroy("stuck"));
            result.Status.ShouldBe(EnvironmentStatus.Failed);
        }

        // The allocation is NOT released by the reset — the resources still need a real destroy (issue #48).
        await using (var ipam = fixture.CreateIpamContext())
        {
            (await ipam.Allocations.AnyAsync(a => a.Name == "stuck")).ShouldBeTrue();
        }

        // The guard is released: the sanctioned destroy path now proceeds without any raw workflow dispatch.
        using (var scope = _host.Services.CreateScope())
        {
            var spoke = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();
            var destroyed = await spoke.DestroyAsync(EnvRef.ById(envId), Confirmation.ForDestroy("stuck"));
            destroyed.Status.ShouldBe(EnvironmentStatus.Destroying);
        }

        await WaitForDispatchAsync(envId, RunPhase.Destroy);
    }

    [Fact]
    public async Task Plan_reset_of_a_terminal_environment_reports_nothing_to_reset()
    {
        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<IEnvironmentRegistry>();
            envId = await registry.BeginCreateAsync(EnvironmentKind.Spoke, Subscription, "westus3", "healthy", "owner");
            await registry.TransitionAsync(envId, EnvironmentStatus.Active);
        }

        using (var scope = _host.Services.CreateScope())
        {
            var maintenance = scope.ServiceProvider.GetRequiredService<IEnvironmentMaintenanceVerbs>();
            await Should.ThrowAsync<EnvironmentNotWedgedException>(() =>
                maintenance.PlanResetAsync(EnvRef.ById(envId)));

            // Even a confirmed reset is a harmless no-op on a terminal environment (idempotent).
            var result = await maintenance.ResetAsync(EnvRef.ById(envId), Confirmation.ForDestroy("healthy"));
            result.Status.ShouldBe(EnvironmentStatus.Active);
        }
    }

    /// <summary>Vends a spoke through the apply path and leaves it wedged in <c>Provisioning</c> (never reconciled).</summary>
    private async Task<Guid> WedgedSpokeAsync(string name)
    {
        Guid envId;
        using (var scope = _host.Services.CreateScope())
        {
            var ledger = scope.ServiceProvider.GetRequiredService<IIpamLedger>();
            var spoke = scope.ServiceProvider.GetRequiredService<ISpokeVerbs>();

            await ledger.RegisterRegionAsync("westus3", 2);
            var result = await spoke.CreateAsync(
                new SpokeCreateRequest(Subscription, "westus3", name, Size: 24),
                Confirmation.ForApply());
            envId = result.EnvId;
        }

        // The dispatcher is a substitute, so no run ever completes — the environment sticks at Provisioning.
        await WaitForStatusAsync(envId, EnvironmentStatus.Provisioning);
        return envId;
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
}
