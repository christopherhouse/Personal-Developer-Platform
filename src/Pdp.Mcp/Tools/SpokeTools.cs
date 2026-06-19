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
        "Plan a spoke vend: returns the plan and a single-use confirmation token. Creates NOTHING. " +
        "The CIDR is allocated live from the IPAM ledger by size — there is no CIDR input. " +
        "Call ApplySpokeVend with the returned token and the same spoke name to proceed.")]
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
        var token = tokens.Issue(ConfirmationOperation.SpokeVend, spokeName);
        return new McpPlanResult(token, spokeName, plan);
    }

    [McpServerTool, Description(
        "Apply a previously planned spoke vend. Requires the confirmation token from PlanSpokeVend and the " +
        "exact same spoke name; rejected if the token is missing, expired, already used, or the name does " +
        "not match. Dispatches the vend and returns the tracked run handle.")]
    public async Task<VerbResult> ApplySpokeVend(
        [Description("The confirmation token returned by PlanSpokeVend.")] string confirmationToken,
        [Description("Target subscription id (Azure GUID) — must match the planned vend.")] string subscription,
        [Description("Registered region, e.g. westus3 — must match the planned vend.")] string region,
        [Description("Spoke name — must match the token's target verbatim.")] string spokeName,
        ClaimsPrincipal? caller,
        [Description("Block size (prefix length); default /24.")] int size = 24,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        tokens.Validate(confirmationToken, ConfirmationOperation.SpokeVend, spokeName);
        var request = new SpokeCreateRequest(subscription, region, spokeName, size);
        return await spoke.CreateAsync(request, Confirmation.ForApply(), cancellationToken).ConfigureAwait(false);
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
        var token = tokens.Issue(ConfirmationOperation.SpokeDestroy, spokeName);
        return new McpPlanResult(token, spokeName, plan);
    }

    [McpServerTool, Description(
        "DANGER: destroys a spoke and releases its IPAM allocation. Requires the confirmation token from " +
        "PlanSpokeDestroy AND the exact spoke name restated verbatim. Rejected if the token is missing, " +
        "expired, already used, or the name does not match the token. There is no single-call destroy.")]
    public async Task<VerbResult> DestroySpoke(
        [Description("The confirmation token returned by PlanSpokeDestroy.")] string confirmationToken,
        [Description("Subscription id the spoke lives in.")] string subscription,
        [Description("Spoke name to destroy — must match the token's target verbatim.")] string spokeName,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        tokens.Validate(confirmationToken, ConfirmationOperation.SpokeDestroy, spokeName);
        var target = EnvRef.ByNaturalKey(EnvironmentKind.Spoke, subscription, spokeName);
        return await spoke.DestroyAsync(target, Confirmation.ForDestroy(spokeName), cancellationToken)
            .ConfigureAwait(false);
    }
}
