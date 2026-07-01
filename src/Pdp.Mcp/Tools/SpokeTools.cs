using System.ComponentModel;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.Mcp.Auth;
using Pdp.Mcp.Confirm;

namespace Pdp.Mcp.Tools;

/// <summary>
/// The spoke MCP tools — a <b>thin adapter</b> over <see cref="ISpokeVerbs"/> (contracts/mcp-tool-surface.md;
/// data-model §4). Each method asserts the owner, calls the spec-006 verb 1:1, and surfaces the typed
/// result — no domain logic, no new capability (SC-003). Mutations are split into a <c>Plan</c> tool (issues
/// a confirmation token; mutates nothing) and an <c>Apply</c>/<c>Destroy</c> tool (requires that token + the
/// verbatim target), so a chat turn can never mutate without an explicit, target-restating confirmation
/// (Article VIII / FR-012/FR-013).
/// </summary>
[McpServerToolType]
public sealed class SpokeTools(ISpokeVerbs spoke, IConfirmationTokens tokens, IOptions<McpAuthOptions> auth)
    : OwnerTool(auth)
{
    [McpServerTool, Description(
        "Plan a spoke vend: dispatches the plan run and returns IMMEDIATELY with a single-use confirmation " +
        "token and the env_id. Creates NOTHING and does not wait for the plan to finish. The CIDR is " +
        "allocated live from the IPAM ledger by size — there is no CIDR input. NEXT: poll ShowEnvironment or " +
        "RunStatus until the plan run reports Succeeded and review the surfaced plan, THEN call ApplySpokeVend " +
        "with the returned token and the same spoke name.")]
    public async Task<McpPlanResult> PlanSpokeVend(
        [Description("Target subscription id (Azure GUID) to vend the spoke into.")] string subscription,
        [Description("Registered region with a deployed fabric, e.g. westus3.")] string region,
        [Description("Spoke name, unique within the subscription ([a-z0-9-], 1-24).")] string spokeName,
        ClaimsPrincipal? caller,
        [Description("Block size (prefix length) to allocate from the ledger; default /24.")] int size = 24,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        var request = new SpokeCreateRequest(subscription, region, spokeName, size);
        var plan = await spoke.PlanCreateAsync(request, cancellationToken).ConfigureAwait(false);
        // Stash the planned request on the token so ApplySpokeVend needs only the token + the verbatim name.
        var token = tokens.Issue(ConfirmationOperation.SpokeVend, spokeName, request);
        return new McpPlanResult(token, spokeName, plan);
    }

    [McpServerTool, Description(
        "Apply a previously planned spoke vend. Requires ONLY the confirmation token from PlanSpokeVend and " +
        "the exact same spoke name restated verbatim — the subscription, region, and size come from the plan " +
        "(via the token), so the apply uses exactly what you reviewed. If the plan run has not finished yet " +
        "you get a clear 'plan not ready' message — check ShowEnvironment/RunStatus and retry; your token is " +
        "preserved. Once the plan has succeeded, dispatches the vend and returns the tracked run handle.")]
    public async Task<VerbResult> ApplySpokeVend(
        [Description("The confirmation token returned by PlanSpokeVend.")] string confirmationToken,
        [Description("Spoke name — must match the token's target verbatim (the Article VIII restatement).")] string spokeName,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        // The token carries the exact planned request — no need to re-enter subscription/region/size.
        var request = tokens.Redeem<SpokeCreateRequest>(confirmationToken, ConfirmationOperation.SpokeVend, spokeName);
        var result = await InvokeGatedAsync(() => spoke.CreateAsync(request, Confirmation.ForApply(), cancellationToken))
            .ConfigureAwait(false);
        tokens.Consume(confirmationToken); // consumed ONLY now that the apply actually dispatched
        return result;
    }

    [McpServerTool, Description(
        "Plan a spoke destroy: returns the destroy preview and a single-use confirmation token. " +
        "Destroys NOTHING. Call DestroySpoke with the returned token and the exact spoke name to proceed.")]
    public async Task<McpPlanResult> PlanSpokeDestroy(
        [Description("Subscription id the spoke lives in.")] string subscription,
        [Description("Spoke name to destroy.")] string spokeName,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        var target = EnvRef.ByNaturalKey(EnvironmentKind.Spoke, subscription, spokeName);
        var plan = await spoke.PlanDestroyAsync(target, cancellationToken).ConfigureAwait(false);
        // Stash the resolved target on the token so DestroySpoke needs only the token + the verbatim name.
        var token = tokens.Issue(ConfirmationOperation.SpokeDestroy, spokeName, target);
        return new McpPlanResult(token, spokeName, plan);
    }

    [McpServerTool, Description(
        "DANGER: destroys a spoke and releases its IPAM allocation. Requires ONLY the confirmation token from " +
        "PlanSpokeDestroy AND the exact spoke name restated verbatim — the subscription comes from the plan " +
        "(via the token). Rejected if the token is missing, expired, already used, or the name does not match " +
        "the token. There is no single-call destroy.")]
    public async Task<VerbResult> DestroySpoke(
        [Description("The confirmation token returned by PlanSpokeDestroy.")] string confirmationToken,
        [Description("Spoke name to destroy — must match the token's target verbatim (the Article VIII restatement).")] string spokeName,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        // The token carries the resolved target (kind/subscription/name) — no need to re-enter the subscription.
        var target = tokens.Redeem<EnvRef>(confirmationToken, ConfirmationOperation.SpokeDestroy, spokeName);
        var result = await InvokeGatedAsync(() =>
                spoke.DestroyAsync(target, Confirmation.ForDestroy(spokeName), cancellationToken))
            .ConfigureAwait(false);
        tokens.Consume(confirmationToken); // consumed ONLY now that the destroy actually dispatched
        return result;
    }
}
