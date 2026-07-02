using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pdp.ControlPlane.Verbs;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.Mcp.Auth;
using Pdp.Mcp.Confirm;

namespace Pdp.Mcp.Tools;

/// <summary>
/// What <c>PlanWorkloadDeploy</c> returns (spec 008, contracts/workload-verbs.md): either a surfaced
/// plan + the single-use confirmation token (the happy path), or — when the parameters failed the
/// archetype's JSON schema — the per-parameter <see cref="ParameterViolations"/> <b>as data</b> with
/// <b>no token</b>, so the model can correct the offending parameters and re-plan (FR-003/SC-003).
/// Immutable; System.Text.Json serializable.
/// </summary>
/// <param name="ConfirmationToken">The opaque, single-use, ~15-minute token; null when validation failed.</param>
/// <param name="TargetName">The workload name the confirming call must restate verbatim.</param>
/// <param name="Plan">The verb-layer plan result; null when validation failed (nothing dispatched).</param>
/// <param name="ParameterViolations">Schema violations naming each offending parameter; null on success.</param>
public sealed record McpWorkloadPlanResult(
    string? ConfirmationToken,
    string TargetName,
    PlanResult? Plan,
    IReadOnlyList<ParameterViolation>? ParameterViolations);

/// <summary>
/// The workload MCP tools (spec 008) — a <b>thin adapter</b> over <see cref="IWorkloadVerbs"/>,
/// exactly the <see cref="SpokeTools"/> shape: every mutation splits into a <c>Plan</c> tool (issues
/// a confirmation token; mutates nothing) and an <c>Apply</c> tool (requires that token + the
/// verbatim workload name), so a chat turn can never mutate without an explicit, target-restating
/// confirmation (Article VIII). Schema-invalid parameters return violations as data with <b>no</b>
/// token — nothing is recorded or dispatched (FR-003). The catalog itself has no mutating tool
/// anywhere: chat can never alter deployable truth (clarify 2026-07-02).
/// </summary>
[McpServerToolType]
public sealed class WorkloadTools(IWorkloadVerbs workloads, IConfirmationTokens tokens, IOptions<McpAuthOptions> auth)
    : OwnerTool(auth)
{
    [McpServerTool, Description(
        "Plan a workload deploy from the archetype catalog into an existing Active spoke: dispatches the " +
        "plan run and returns IMMEDIATELY with a single-use confirmation token and the env_id. Deploys " +
        "NOTHING and does not wait for the plan to finish. The archetype VERSION is never an input — the " +
        "newest active version is resolved and stamped server-side. If the parameters fail the archetype's " +
        "JSON schema, the result carries parameterViolations (one per offending parameter) and NO token — " +
        "fix the parameters and re-plan. NEXT: poll ShowEnvironment or RunStatus until the plan run reports " +
        "Succeeded and review the surfaced plan, THEN call ApplyWorkloadDeploy with the returned token and " +
        "the same workload name.")]
    public async Task<McpWorkloadPlanResult> PlanWorkloadDeploy(
        [Description("Target subscription id (Azure GUID) — the subscription the spoke lives in.")] string subscription,
        [Description("Existing Active spoke the workload deploys into.")] string spokeName,
        [Description("Workload name, unique within the subscription ([a-z0-9-], 1-24).")] string workloadName,
        [Description("Catalog archetype name, e.g. container-app-sql. The version is resolved server-side.")] string archetype,
        [Description("Workload environment (the pdp-env tag), e.g. dev ([a-z0-9-], 1-16).")] string environment,
        [Description("Archetype parameters as a JSON object, validated against the archetype's schema (e.g. {\"containerImage\":\"...\"}).")] string parametersJson,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        var request = new WorkloadDeployRequest(
            subscription, spokeName, workloadName, archetype, environment, ParseParameters(parametersJson));

        try
        {
            var plan = await workloads.PlanDeployAsync(request, cancellationToken).ConfigureAwait(false);
            // Stash the planned request on the token so ApplyWorkloadDeploy needs only the token + the
            // verbatim name — the apply uses exactly what was planned.
            var token = tokens.Issue(ConfirmationOperation.WorkloadDeploy, workloadName, request);
            return new McpWorkloadPlanResult(token, workloadName, plan, ParameterViolations: null);
        }
        catch (WorkloadParameterValidationException ex)
        {
            // Schema failures are DATA, not an error: the model reads the violations, fixes the
            // parameters, and re-plans. No token exists, so nothing can be applied (SC-003).
            return new McpWorkloadPlanResult(
                ConfirmationToken: null, workloadName, Plan: null, ex.Violations);
        }
        catch (Exception ex) when (ex is ArchetypeNotDeployableException
                                       or SpokeNotActiveException
                                       or EnvironmentNotFoundException
                                       or WorkloadParametersChangedException)
        {
            // Catalog/spoke/repeat-deploy refusals carry actionable messages — surface them verbatim.
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool, Description(
        "Apply a previously planned workload deploy. Requires ONLY the confirmation token from " +
        "PlanWorkloadDeploy and the exact same workload name restated verbatim — the subscription, spoke, " +
        "archetype, and parameters come from the plan (via the token), so the apply dispatches exactly what " +
        "you reviewed. If the plan run has not finished yet you get a clear 'plan not ready' message — check " +
        "ShowEnvironment/RunStatus and retry; your token is preserved. Once the plan has succeeded, " +
        "dispatches the deploy and returns the tracked run handle.")]
    public async Task<VerbResult> ApplyWorkloadDeploy(
        [Description("The confirmation token returned by PlanWorkloadDeploy.")] string confirmationToken,
        [Description("Workload name — must match the token's target verbatim (the Article VIII restatement).")] string workloadName,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        // The token carries the exact planned request — no need to re-enter any deploy input.
        var request = tokens.Redeem<WorkloadDeployRequest>(
            confirmationToken, ConfirmationOperation.WorkloadDeploy, workloadName);
        var result = await InvokeGatedAsync(() =>
                workloads.DeployAsync(request, Confirmation.ForApply(), cancellationToken))
            .ConfigureAwait(false);
        tokens.Consume(confirmationToken); // consumed ONLY now that the apply actually dispatched
        return result;
    }

    /// <summary>
    /// Parses the tool's <c>parametersJson</c> argument to the verb layer's <see cref="JsonObject"/>.
    /// An empty/whitespace argument means "all defaults" (<c>{}</c>); anything that is not a JSON
    /// object is rejected before any verb runs.
    /// </summary>
    private static JsonObject ParseParameters(string parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(parametersJson) as JsonObject
                ?? throw new McpException("parametersJson must be a JSON object, e.g. {\"containerImage\":\"...\"}.");
        }
        catch (JsonException ex)
        {
            throw new McpException($"parametersJson is not valid JSON: {ex.Message}");
        }
    }
}
