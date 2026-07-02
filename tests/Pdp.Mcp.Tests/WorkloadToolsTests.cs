using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using NSubstitute;
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
/// Spec-008 T030 — the workload MCP tools are a thin, Article VIII-gated adapter over
/// <see cref="IWorkloadVerbs"/>: <c>PlanWorkloadDeploy</c> issues a single-use token bound to the
/// verbatim workload name; <c>ApplyWorkloadDeploy</c> redeems it and dispatches exactly the planned
/// request; schema violations come back <b>as data with no token</b> (FR-003/SC-003), so an invalid
/// deploy can never be applied. Verbs are substituted — the verb behavior itself is covered by the
/// Verbs.Tests suite.
/// </summary>
public sealed class WorkloadToolsTests
{
    private const string OwnerOid = "owner-oid-0001";
    private const string Subscription = "8bd05b2f-62c5-4def-9869-f0617ebb3970";

    private static readonly IOptions<McpAuthOptions> Auth =
        Options.Create(new McpAuthOptions { OwnerOid = OwnerOid });

    private static ClaimsPrincipal Owner =>
        new(new ClaimsIdentity([new Claim("oid", OwnerOid)], "test"));

    private static PlanResult SomePlan() =>
        new(Guid.CreateVersion7(), RunPhase.Plan, PlanSummary: null,
            new Dictionary<string, string> { ["archetype_ref"] = "archetype/container-app-sql/v1.0.0" },
            RunUrl: null);

    private static VerbResult Dispatched() =>
        new(Guid.CreateVersion7(), EnvironmentStatus.Provisioning, Guid.CreateVersion7(),
            GitHubRunUrl: null, RunOutcome.Dispatched, Messages: []);

    private static Task<McpWorkloadPlanResult> PlanAsync(WorkloadTools tools) =>
        tools.PlanWorkloadDeploy(
            Subscription, "app1", "demo-api", "container-app-sql", "dev",
            """{"containerImage":"nginx:latest"}""", Owner);

    [Fact]
    public async Task PlanWorkloadDeploy_calls_the_verb_once_and_issues_a_token()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        verbs.PlanDeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        var tools = new WorkloadTools(verbs, new ConfirmationTokenService(), Auth);

        var result = await PlanAsync(tools);

        await verbs.Received(1).PlanDeployAsync(
            Arg.Is<WorkloadDeployRequest>(r =>
                r.Subscription == Subscription &&
                r.SpokeName == "app1" &&
                r.WorkloadName == "demo-api" &&
                r.Archetype == "container-app-sql" &&
                r.Environment == "dev" &&
                r.Parameters["containerImage"]!.GetValue<string>() == "nginx:latest"),
            Arg.Any<CancellationToken>());
        result.ConfirmationToken.ShouldNotBeNullOrWhiteSpace();
        result.TargetName.ShouldBe("demo-api");
        result.Plan.ShouldNotBeNull();
        result.ParameterViolations.ShouldBeNull();
    }

    [Fact]
    public async Task PlanWorkloadDeploy_returns_schema_violations_as_data_with_no_token()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        verbs.PlanDeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<PlanResult>>(_ => throw new WorkloadParameterValidationException(
                "container-app-sql", "v1.0.0",
                [new ParameterViolation("/cpu", "enum", "value is not one of 0.25, 0.5, 1")]));
        var tools = new WorkloadTools(verbs, new ConfirmationTokenService(), Auth);

        var result = await PlanAsync(tools);

        // Violations are DATA the model corrects from; no token exists, so nothing can be applied.
        result.ConfirmationToken.ShouldBeNull();
        result.Plan.ShouldBeNull();
        result.ParameterViolations.ShouldNotBeNull();
        result.ParameterViolations!.Single().Path.ShouldBe("/cpu");
    }

    [Fact]
    public async Task ApplyWorkloadDeploy_redeems_the_token_and_dispatches_the_planned_request()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        verbs.PlanDeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        verbs.DeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(Dispatched());
        var tools = new WorkloadTools(verbs, new ConfirmationTokenService(), Auth);

        var planned = await PlanAsync(tools);
        var applied = await tools.ApplyWorkloadDeploy(planned.ConfirmationToken!, "demo-api", Owner);

        applied.Status.ShouldBe(EnvironmentStatus.Provisioning);
        await verbs.Received(1).DeployAsync(
            Arg.Is<WorkloadDeployRequest>(r => r.WorkloadName == "demo-api" && r.Archetype == "container-app-sql"),
            Arg.Is<Confirmation>(c => c.IsConfirmed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyWorkloadDeploy_requires_the_verbatim_name_and_a_mismatch_does_not_burn_the_token()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        verbs.PlanDeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<CancellationToken>()).Returns(SomePlan());
        verbs.DeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>())
            .Returns(Dispatched());
        var tools = new WorkloadTools(verbs, new ConfirmationTokenService(), Auth);

        var planned = await PlanAsync(tools);

        await Should.ThrowAsync<McpException>(() =>
            tools.ApplyWorkloadDeploy(planned.ConfirmationToken!, "wrong-name", Owner));
        await verbs.DidNotReceive().DeployAsync(
            Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>());

        // The typo did not consume the confirmation: the verbatim restatement still applies cleanly.
        var applied = await tools.ApplyWorkloadDeploy(planned.ConfirmationToken!, "demo-api", Owner);
        applied.Status.ShouldBe(EnvironmentStatus.Provisioning);
    }

    [Fact]
    public async Task ApplyWorkloadDeploy_without_a_valid_token_never_calls_the_verb()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        var tools = new WorkloadTools(verbs, new ConfirmationTokenService(), Auth);

        await Should.ThrowAsync<McpException>(() =>
            tools.ApplyWorkloadDeploy("forged-token", "demo-api", Owner));

        await verbs.DidNotReceive().DeployAsync(
            Arg.Any<WorkloadDeployRequest>(), Arg.Any<Confirmation>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlanWorkloadDeploy_surfaces_catalog_refusals_as_clear_errors()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        verbs.PlanDeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<PlanResult>>(_ => throw ArchetypeNotDeployableException.Retired("container-app-sql"));
        var tools = new WorkloadTools(verbs, new ConfirmationTokenService(), Auth);

        var ex = await Should.ThrowAsync<McpException>(() => PlanAsync(tools));
        ex.Message.ShouldContain("retired");
    }

    [Fact]
    public async Task PlanWorkloadDeploy_rejects_malformed_parameters_json_before_any_verb_runs()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        var tools = new WorkloadTools(verbs, new ConfirmationTokenService(), Auth);

        await Should.ThrowAsync<McpException>(() =>
            tools.PlanWorkloadDeploy(
                Subscription, "app1", "demo-api", "container-app-sql", "dev", "not json", Owner));

        await verbs.DidNotReceive().PlanDeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tools_refuse_a_non_owner_before_any_verb_runs()
    {
        var verbs = Substitute.For<IWorkloadVerbs>();
        var tools = new WorkloadTools(verbs, new ConfirmationTokenService(), Auth);
        var stranger = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "not-the-owner")], "test"));

        await Should.ThrowAsync<McpException>(() =>
            tools.PlanWorkloadDeploy(
                Subscription, "app1", "demo-api", "container-app-sql", "dev", "{}", stranger));

        await verbs.DidNotReceive().PlanDeployAsync(Arg.Any<WorkloadDeployRequest>(), Arg.Any<CancellationToken>());
    }
}
