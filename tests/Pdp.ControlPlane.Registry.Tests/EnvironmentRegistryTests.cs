using Microsoft.EntityFrameworkCore;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;
using Shouldly;

namespace Pdp.ControlPlane.Registry.Tests;

/// <summary>
/// The registry's intent invariants on real Postgres (T028a): <b>idempotent natural-key convergence</b>
/// (re-create of <c>(kind, subscription, name)</c> returns the same <c>env_id</c> — FR-022), the
/// <b>single-flight guard</b> (a non-terminal environment rejects a new mutation — FR-022a), and the
/// lifecycle <b>status transitions</b>. These depend on the unique natural-key index and so are
/// untestable in-memory (plan §Testing).
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class EnvironmentRegistryTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "11111111-1111-1111-1111-111111111111";
    private const string Region = "westus3";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Re_create_of_a_terminal_environment_converges_on_the_same_env_id()
    {
        Guid firstId;
        await using (var context = fixture.CreateRegistryContext())
        {
            var registry = new EnvironmentRegistry(context);
            firstId = await registry.BeginCreateAsync(EnvironmentKind.Spoke, Subscription, Region, "app1", "owner");
            // Drive it to a terminal state so a re-create is permitted (no in-flight run).
            await registry.TransitionAsync(firstId, EnvironmentStatus.Active);
        }

        await using (var context = fixture.CreateRegistryContext())
        {
            var registry = new EnvironmentRegistry(context);
            var secondId = await registry.BeginCreateAsync(EnvironmentKind.Spoke, Subscription, Region, "app1", "owner");

            secondId.ShouldBe(firstId);
        }

        // Exactly one row for the natural key — convergence, not a duplicate surrogate.
        await using var verify = fixture.CreateRegistryContext();
        var matches = await verify.Environments
            .CountAsync(e => e.Subscription == Subscription && e.Name == "app1");
        matches.ShouldBe(1);
    }

    [Fact]
    public async Task Begin_create_on_a_non_terminal_environment_is_rejected_single_flight()
    {
        await using var context = fixture.CreateRegistryContext();
        var registry = new EnvironmentRegistry(context);

        var envId = await registry.BeginCreateAsync(EnvironmentKind.Spoke, Subscription, Region, "app2", "owner");
        // Still Requested (non-terminal) — a second mutation must be refused.

        await using var second = fixture.CreateRegistryContext();
        var contender = new EnvironmentRegistry(second);

        var ex = await Should.ThrowAsync<OperationInProgressException>(
            () => contender.BeginCreateAsync(EnvironmentKind.Spoke, Subscription, Region, "app2", "owner"));

        ex.EnvId.ShouldBe(envId);
        ex.Status.ShouldBe(EnvironmentStatus.Requested);
    }

    [Fact]
    public async Task Status_transitions_walk_the_create_and_destroy_lifecycle()
    {
        await using var context = fixture.CreateRegistryContext();
        var registry = new EnvironmentRegistry(context);

        var envId = await registry.BeginCreateAsync(EnvironmentKind.Spoke, Subscription, Region, "app3", "owner");

        await registry.TransitionAsync(envId, EnvironmentStatus.Provisioning);
        await registry.TransitionAsync(envId, EnvironmentStatus.Active);
        (await registry.FindByIdAsync(envId))!.Status.ShouldBe(EnvironmentStatus.Active);

        await registry.TransitionAsync(envId, EnvironmentStatus.Destroying);
        await registry.TransitionAsync(envId, EnvironmentStatus.Destroyed);
        (await registry.FindByIdAsync(envId))!.Status.ShouldBe(EnvironmentStatus.Destroyed);
    }

    [Fact]
    public async Task GetLatestRunAsync_returns_only_the_newest_run_and_null_when_none_exist()
    {
        await using var context = fixture.CreateRegistryContext();
        var registry = new EnvironmentRegistry(context);

        var envId = await registry.BeginCreateAsync(EnvironmentKind.Spoke, Subscription, Region, "app4", "owner");

        // No runs yet — should return null.
        var noRun = await registry.GetLatestRunAsync(envId);
        noRun.ShouldBeNull();

        var now = DateTimeOffset.UtcNow;
        var olderRunId = Guid.CreateVersion7();
        var newerRunId = Guid.CreateVersion7();

        // Insert two runs with different DispatchedAt timestamps directly so we can control ordering.
        context.ProvisioningRuns.AddRange(
            new ProvisioningRun
            {
                RunId = olderRunId,
                EnvId = envId,
                Phase = RunPhase.Plan,
                WorkflowFile = "spoke-vend.yml",
                DispatchInputs = "{}",
                Outcome = RunOutcome.Succeeded,
                DispatchedAt = now.AddMinutes(-5),
            },
            new ProvisioningRun
            {
                RunId = newerRunId,
                EnvId = envId,
                Phase = RunPhase.Apply,
                WorkflowFile = "spoke-vend.yml",
                DispatchInputs = "{}",
                Outcome = RunOutcome.Dispatched,
                DispatchedAt = now,
            });
        await context.SaveChangesAsync();

        // GetLatestRunAsync must return the newer run without loading both rows.
        await using var readContext = fixture.CreateRegistryContext();
        var readRegistry = new EnvironmentRegistry(readContext);
        var latest = await readRegistry.GetLatestRunAsync(envId);

        latest.ShouldNotBeNull();
        latest!.RunId.ShouldBe(newerRunId);
        latest.Phase.ShouldBe(RunPhase.Apply);
    }
}
