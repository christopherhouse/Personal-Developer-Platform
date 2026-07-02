using System.ComponentModel;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.Mcp.Auth;
using Pdp.Mcp.Confirm;

namespace Pdp.Mcp.Tools;

/// <summary>
/// The environment-maintenance MCP tools — a <b>thin adapter</b> over <see cref="IEnvironmentMaintenanceVerbs"/>
/// (issue #48). The in-product recovery path for a unit whose dispatched run died without ever recording a
/// terminal outcome: it stays non-terminal forever and the single-flight guard (FR-022a) then rejects
/// <b>every</b> mutating verb — including a destroy — so it can never be torn down through the sanctioned
/// path. Reset clears that guard (registry status only), after which the normal <c>PlanSpokeDestroy</c> /
/// <c>DestroySpoke</c> flow tears the resources down. Owner-gated; split into a <c>Plan</c> tool (issues a
/// token; mutates nothing) and a <c>Reset</c> tool (requires that token + the verbatim target), so a chat
/// turn can never reset without an explicit, target-restating confirmation (Article VIII / FR-012/FR-013).
/// </summary>
[McpServerToolType]
public sealed class MaintenanceTools(
    IEnvironmentMaintenanceVerbs maintenance,
    IConfirmationTokens tokens,
    IOptions<McpAuthOptions> auth) : OwnerTool(auth)
{
    [McpServerTool, Description(
        "Plan a recovery reset of a WEDGED environment — one stuck non-terminal (Provisioning/Destroying/" +
        "Requested) because its run died without recording a terminal outcome, which blocks every mutating " +
        "verb (including destroy) via the single-flight guard. Returns the current status, the latest run, " +
        "and a single-use confirmation token. Changes NOTHING. FIRST confirm the unit is really stuck with " +
        "ShowEnvironment (it reconciles the run); if it is still non-terminal, call ResetEnvironment with the " +
        "returned token and the exact same name to force it to 'Failed', then destroy it normally.")]
    public async Task<McpResetPlan> PlanResetEnvironment(
        [Description("Environment ref: an env_id (UUID) or kind:subscription:name (e.g. spoke:<sub>:app5).")] string environment,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        var target = ParseEnvRef(environment);
        var preview = await maintenance.PlanResetAsync(target, cancellationToken).ConfigureAwait(false);
        // Stash the resolved target on the token so ResetEnvironment needs only the token + the verbatim name.
        var token = tokens.Issue(ConfirmationOperation.EnvironmentReset, preview.Name, target);
        return new McpResetPlan(token, preview.Name, preview);
    }

    [McpServerTool, Description(
        "Force-reset a wedged environment to 'Failed', releasing the single-flight guard so it can be " +
        "destroyed through the normal path. Requires ONLY the confirmation token from PlanResetEnvironment " +
        "AND the exact same name restated verbatim. Changes REGISTRY STATUS ONLY — it does NOT touch Azure " +
        "resources and does NOT release the IPAM allocation (destroy the unit afterward to reclaim those). " +
        "Rejected if the token is missing, expired, already used, or the name does not match.")]
    public async Task<VerbResult> ResetEnvironment(
        [Description("The confirmation token returned by PlanResetEnvironment.")] string confirmationToken,
        [Description("Environment name to reset — must match the token's target verbatim (the Article VIII restatement).")] string name,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        // The token carries the resolved target (kind/subscription/name or env_id) captured at plan time.
        var target = tokens.Redeem<EnvRef>(confirmationToken, ConfirmationOperation.EnvironmentReset, name);
        var result = await maintenance
            .ResetAsync(target, Confirmation.ForDestroy(name), cancellationToken)
            .ConfigureAwait(false);
        tokens.Consume(confirmationToken); // consumed ONLY now that the reset actually applied
        return result;
    }

    private static EnvRef ParseEnvRef(string environment)
    {
        if (!EnvRef.TryParse(environment, out var reference) || reference is null)
        {
            throw new McpException(
                $"'{environment}' is not a valid env ref. Use an env_id (UUID) or kind:subscription:name.");
        }

        return reference;
    }
}
