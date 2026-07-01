using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using NSubstitute;
using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.Mcp.Auth;
using Pdp.Mcp.Confirm;
using Pdp.Mcp.Tools;
using Shouldly;

namespace Pdp.Mcp.Tests;

/// <summary>
/// T030 — the MCP tools are a <b>thin adapter</b> over the spec-006 verb layer (SC-003): each tool calls its
/// verb 1:1 with no domain logic, surfacing the structured result. Verbs are substituted (NSubstitute) so
/// the assertions are purely about the adapter — the verb behaviour itself is covered by the spec-006 suites.
/// Also verifies the owner gate (EnsureOwner) refuses a non-owner before any verb runs.
/// </summary>
public sealed class ToolVerbAdapterTests
{
    private const string OwnerOid = "owner-oid-0001";
    private const string Subscription = "8bd05b2f-62c5-4def-9869-f0617ebb3970";

    private static readonly IOptions<McpAuthOptions> Auth =
        Options.Create(new McpAuthOptions { OwnerOid = OwnerOid });

    private static ClaimsPrincipal Caller(string oid) =>
        new(new ClaimsIdentity([new Claim("oid", oid)], "test"));

    private static ClaimsPrincipal Owner => Caller(OwnerOid);

    private static PlanResult SomePlan() =>
        new(Guid.CreateVersion7(), RunPhase.Plan, PlanSummary: "no changes", new Dictionary<string, string>(), RunUrl: null);

    private static VerbResult SomeResult(EnvironmentStatus status) =>
        new(Guid.CreateVersion7(), status, RunId: null, GitHubRunUrl: null, Outcome: null, Messages: []);

    // --- Spoke: vend (plan → apply) ---------------------------------------------------------------------

    [Fact]
    public async Task PlanSpokeVend_calls_the_verb_once_and_issues_a_token()
    {
        var spoke = Substitute.For<ISpokeVerbs>();
        spoke.PlanCreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        var tools = new SpokeTools(spoke, new ConfirmationTokenService(), Auth);

        var result = await tools.PlanSpokeVend(Subscription, "westus3", "app5", Owner);

        await spoke.Received(1).PlanCreateAsync(
            Arg.Is<SpokeCreateRequest>(r => r.Subscription == Subscription && r.Region == "westus3" && r.Name == "app5" && r.Size == 24),
            Arg.Any<CancellationToken>());
        result.ConfirmationToken.ShouldNotBeNullOrWhiteSpace();
        result.TargetName.ShouldBe("app5");
    }

    [Fact]
    public async Task ApplySpokeVend_validates_the_token_then_calls_CreateAsync_once()
    {
        var spoke = Substitute.For<ISpokeVerbs>();
        spoke.PlanCreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        spoke.CreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(SomeResult(EnvironmentStatus.Provisioning));
        var tools = new SpokeTools(spoke, new ConfirmationTokenService(), Auth);

        var planned = await tools.PlanSpokeVend(Subscription, "westus3", "app5", Owner);
        var applied = await tools.ApplySpokeVend(planned.ConfirmationToken, "app5", Owner);

        applied.Status.ShouldBe(EnvironmentStatus.Provisioning);
        await spoke.Received(1).CreateAsync(
            Arg.Is<SpokeCreateRequest>(r => r.Name == "app5"),
            Arg.Is<Confirmation>(c => c.IsConfirmed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySpokeVend_without_a_valid_token_never_calls_the_verb()
    {
        var spoke = Substitute.For<ISpokeVerbs>();
        var tools = new SpokeTools(spoke, new ConfirmationTokenService(), Auth);

        await Should.ThrowAsync<McpException>(() =>
            tools.ApplySpokeVend("forged-token", "app5", Owner));

        await spoke.DidNotReceive().CreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplySpokeVend_when_the_plan_is_not_ready_throws_McpException_and_preserves_the_token()
    {
        var spoke = Substitute.For<ISpokeVerbs>();
        spoke.PlanCreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        var tools = new SpokeTools(spoke, new ConfirmationTokenService(), Auth);

        var planned = await tools.PlanSpokeVend(Subscription, "westus3", "app5", Owner);

        // The plan run hasn't succeeded yet → the verb rejects with PlanNotReadyException, which the adapter
        // surfaces as a clear McpException — NOT a single-flight error — without consuming the token.
        spoke.CreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<VerbResult>(
                new PlanNotReadyException(Guid.CreateVersion7(), EnvironmentStatus.Provisioning, RunPhase.Plan)));
        await Should.ThrowAsync<McpException>(() =>
            tools.ApplySpokeVend(planned.ConfirmationToken, "app5", Owner));

        // Token preserved: once the plan succeeds and the verb dispatches, the SAME token applies cleanly.
        spoke.CreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(SomeResult(EnvironmentStatus.Provisioning));
        var applied = await tools.ApplySpokeVend(planned.ConfirmationToken, "app5", Owner);
        applied.Status.ShouldBe(EnvironmentStatus.Provisioning);
    }

    // --- Spoke: destroy (plan → destroy, token + verbatim target) ---------------------------------------

    [Fact]
    public async Task DestroySpoke_validates_the_token_then_calls_DestroyAsync_with_the_restated_target()
    {
        var spoke = Substitute.For<ISpokeVerbs>();
        spoke.PlanDestroyAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        spoke.DestroyAsync(Arg.Any<EnvRef>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(SomeResult(EnvironmentStatus.Destroying));
        var tools = new SpokeTools(spoke, new ConfirmationTokenService(), Auth);

        var planned = await tools.PlanSpokeDestroy(Subscription, "app5", Owner);
        var destroyed = await tools.DestroySpoke(planned.ConfirmationToken, "app5", Owner);

        destroyed.Status.ShouldBe(EnvironmentStatus.Destroying);
        await spoke.Received(1).DestroyAsync(
            Arg.Is<EnvRef>(e => e.Kind == EnvironmentKind.Spoke && e.Subscription == Subscription && e.Name == "app5"),
            Arg.Is<Confirmation>(c => c.IsConfirmed && c.RestatedTarget == "app5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DestroySpoke_with_a_wrong_name_is_rejected_and_never_calls_the_verb()
    {
        var spoke = Substitute.For<ISpokeVerbs>();
        spoke.PlanDestroyAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        var tools = new SpokeTools(spoke, new ConfirmationTokenService(), Auth);

        var planned = await tools.PlanSpokeDestroy(Subscription, "app5", Owner);

        await Should.ThrowAsync<McpException>(() =>
            tools.DestroySpoke(planned.ConfirmationToken, "app6", Owner));
        await spoke.DidNotReceive().DestroyAsync(Arg.Any<EnvRef>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>());
    }

    // --- Fabric: 1:1 mapping ----------------------------------------------------------------------------

    [Fact]
    public async Task PlanFabricCreate_then_ApplyFabricCreate_call_the_fabric_verb_1to1()
    {
        var fabric = Substitute.For<IFabricVerbs>();
        fabric.PlanCreateAsync(Arg.Any<FabricCreateRequest>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        fabric.CreateAsync(Arg.Any<FabricCreateRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(SomeResult(EnvironmentStatus.Provisioning));
        var tools = new FabricTools(fabric, new ConfirmationTokenService(), new ControlPlaneOptions(), Auth);

        var planned = await tools.PlanFabricCreate("westus3", 7, Owner);
        await tools.ApplyFabricCreate(planned.ConfirmationToken, "westus3", Owner);

        await fabric.Received(1).PlanCreateAsync(Arg.Is<FabricCreateRequest>(r => r.Region == "westus3" && r.RegionIndex == 7), Arg.Any<CancellationToken>());
        await fabric.Received(1).CreateAsync(Arg.Is<FabricCreateRequest>(r => r.Region == "westus3"), Arg.Is<Confirmation>(c => c.IsConfirmed), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DestroyFabric_resolves_the_platform_subscription_natural_key()
    {
        var fabric = Substitute.For<IFabricVerbs>();
        fabric.PlanDestroyAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        fabric.DestroyAsync(Arg.Any<EnvRef>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(SomeResult(EnvironmentStatus.Destroying));
        var controlPlane = new ControlPlaneOptions { PlatformSubscriptionId = "platform-sub-1" };
        var tools = new FabricTools(fabric, new ConfirmationTokenService(), controlPlane, Auth);

        var planned = await tools.PlanFabricDestroy("westus3", Owner);
        await tools.DestroyFabric(planned.ConfirmationToken, "westus3", Owner);

        await fabric.Received(1).DestroyAsync(
            Arg.Is<EnvRef>(e => e.Kind == EnvironmentKind.Fabric && e.Subscription == "platform-sub-1" && e.Name == "westus3"),
            Arg.Is<Confirmation>(c => c.IsConfirmed && c.RestatedTarget == "westus3"),
            Arg.Any<CancellationToken>());
    }

    // --- Maintenance: reset (plan → reset, token + verbatim target) — issue #48 -------------------------

    private static ResetPreview SomeResetPreview(string name) =>
        new(Guid.CreateVersion7(), EnvironmentKind.Spoke, name, EnvironmentStatus.Provisioning, LatestRun: null, Notes: []);

    [Fact]
    public async Task ResetEnvironment_validates_the_token_then_calls_ResetAsync_with_the_restated_target()
    {
        var maintenance = Substitute.For<IEnvironmentMaintenanceVerbs>();
        maintenance.PlanResetAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>()).Returns(SomeResetPreview("app5"));
        maintenance.ResetAsync(Arg.Any<EnvRef>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(SomeResult(EnvironmentStatus.Failed));
        var tools = new MaintenanceTools(maintenance, new ConfirmationTokenService(), Auth);

        var planned = await tools.PlanResetEnvironment($"spoke:{Subscription}:app5", Owner);
        var reset = await tools.ResetEnvironment(planned.ConfirmationToken, "app5", Owner);

        planned.TargetName.ShouldBe("app5");
        reset.Status.ShouldBe(EnvironmentStatus.Failed);
        await maintenance.Received(1).ResetAsync(
            Arg.Is<EnvRef>(e => e.Kind == EnvironmentKind.Spoke && e.Subscription == Subscription && e.Name == "app5"),
            Arg.Is<Confirmation>(c => c.IsConfirmed && c.RestatedTarget == "app5"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetEnvironment_with_a_wrong_name_is_rejected_and_never_calls_the_verb()
    {
        var maintenance = Substitute.For<IEnvironmentMaintenanceVerbs>();
        maintenance.PlanResetAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>()).Returns(SomeResetPreview("app5"));
        var tools = new MaintenanceTools(maintenance, new ConfirmationTokenService(), Auth);

        var planned = await tools.PlanResetEnvironment($"spoke:{Subscription}:app5", Owner);

        await Should.ThrowAsync<McpException>(() =>
            tools.ResetEnvironment(planned.ConfirmationToken, "app6", Owner));
        await maintenance.DidNotReceive().ResetAsync(Arg.Any<EnvRef>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResetEnvironment_without_a_valid_token_never_calls_the_verb()
    {
        var maintenance = Substitute.For<IEnvironmentMaintenanceVerbs>();
        var tools = new MaintenanceTools(maintenance, new ConfirmationTokenService(), Auth);

        await Should.ThrowAsync<McpException>(() =>
            tools.ResetEnvironment("forged-token", "app5", Owner));
        await maintenance.DidNotReceive().ResetAsync(Arg.Any<EnvRef>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_non_owner_caller_is_refused_from_planning_a_reset()
    {
        var maintenance = Substitute.For<IEnvironmentMaintenanceVerbs>();
        var tools = new MaintenanceTools(maintenance, new ConfirmationTokenService(), Auth);

        await Should.ThrowAsync<McpException>(() =>
            tools.PlanResetEnvironment($"spoke:{Subscription}:app5", Caller("someone-else")));
        await maintenance.DidNotReceive().PlanResetAsync(Arg.Any<EnvRef>(), Arg.Any<CancellationToken>());
    }

    // --- Owner gate (defense in depth) ------------------------------------------------------------------

    [Fact]
    public async Task A_non_owner_caller_is_refused_before_any_verb_runs()
    {
        var spoke = Substitute.For<ISpokeVerbs>();
        var tools = new SpokeTools(spoke, new ConfirmationTokenService(), Auth);

        await Should.ThrowAsync<McpException>(() =>
            tools.PlanSpokeVend(Subscription, "westus3", "app5", Caller("someone-else")));

        await spoke.DidNotReceive().PlanCreateAsync(Arg.Any<SpokeCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused()
    {
        var spoke = Substitute.For<ISpokeVerbs>();
        var tools = new SpokeTools(spoke, new ConfirmationTokenService(), Auth);

        await Should.ThrowAsync<McpException>(() =>
            tools.PlanSpokeVend(Subscription, "westus3", "app5", caller: null));
    }
}
