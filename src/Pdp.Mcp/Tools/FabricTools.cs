using System.ComponentModel;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Verbs;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.Mcp.Auth;
using Pdp.Mcp.Confirm;

namespace Pdp.Mcp.Tools;

/// <summary>
/// The regional-fabric MCP tools — a <b>thin adapter</b> over <see cref="IFabricVerbs"/>
/// (contracts/mcp-tool-surface.md; data-model §4). Mirrors <see cref="SpokeTools"/>: owner-gated, 1:1 verb
/// calls, the Article VIII plan→confirm split. A fabric is keyed by its region; the registry natural key is
/// <c>(Fabric, PlatformSubscriptionId, Region)</c>, so destroys resolve the platform subscription from
/// <see cref="ControlPlaneOptions"/> exactly as the CLI does.
/// </summary>
[McpServerToolType]
public sealed class FabricTools(
    IFabricVerbs fabric,
    IConfirmationTokens tokens,
    ControlPlaneOptions controlPlane,
    IOptions<McpAuthOptions> auth) : OwnerTool(auth)
{
    [McpServerTool, Description(
        "Plan a regional fabric create: returns the plan and a single-use confirmation token. " +
        "Creates NOTHING. Call ApplyFabricCreate with the returned token and the same region to proceed.")]
    public async Task<McpPlanResult> PlanFabricCreate(
        [Description("Azure region to stand the fabric up in, e.g. westus3.")] string region,
        [Description("The region's /16 index (2nd octet; 1-255). The only address knob.")] int regionIndex,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        var plan = await fabric.PlanCreateAsync(new FabricCreateRequest(region, regionIndex), cancellationToken)
            .ConfigureAwait(false);
        var token = tokens.Issue(ConfirmationOperation.FabricCreate, region);
        return new McpPlanResult(token, region, plan);
    }

    [McpServerTool, Description(
        "Apply a previously planned fabric create. Requires the confirmation token from PlanFabricCreate " +
        "and the exact same region; rejected if the token is missing, expired, used, or the region does not " +
        "match. Dispatches the vend and returns the tracked run handle.")]
    public async Task<VerbResult> ApplyFabricCreate(
        [Description("The confirmation token returned by PlanFabricCreate.")] string confirmationToken,
        [Description("Region — must match the token's target verbatim.")] string region,
        [Description("The region's /16 index — must match the planned create.")] int regionIndex,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        tokens.Validate(confirmationToken, ConfirmationOperation.FabricCreate, region);
        return await fabric.CreateAsync(new FabricCreateRequest(region, regionIndex), Confirmation.ForApply(), cancellationToken)
            .ConfigureAwait(false);
    }

    [McpServerTool, Description(
        "Plan a regional fabric destroy: returns the destroy preview and a single-use confirmation token. " +
        "Destroys NOTHING. Call DestroyFabric with the returned token and the exact region to proceed.")]
    public async Task<McpPlanResult> PlanFabricDestroy(
        [Description("Region of the fabric to destroy.")] string region,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        var target = EnvRef.ByNaturalKey(EnvironmentKind.Fabric, controlPlane.PlatformSubscriptionId, region);
        var plan = await fabric.PlanDestroyAsync(target, cancellationToken).ConfigureAwait(false);
        var token = tokens.Issue(ConfirmationOperation.FabricDestroy, region);
        return new McpPlanResult(token, region, plan);
    }

    [McpServerTool, Description(
        "DANGER: destroys a regional fabric. Requires the confirmation token from PlanFabricDestroy AND the " +
        "exact region restated verbatim. Rejected if the token is missing, expired, used, or the region does " +
        "not match. The region's hub carve-out in the IPAM ledger survives. There is no single-call destroy.")]
    public async Task<VerbResult> DestroyFabric(
        [Description("The confirmation token returned by PlanFabricDestroy.")] string confirmationToken,
        [Description("Region of the fabric to destroy — must match the token's target verbatim.")] string region,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        tokens.Validate(confirmationToken, ConfirmationOperation.FabricDestroy, region);
        var target = EnvRef.ByNaturalKey(EnvironmentKind.Fabric, controlPlane.PlatformSubscriptionId, region);
        return await fabric.DestroyAsync(target, Confirmation.ForDestroy(region), cancellationToken)
            .ConfigureAwait(false);
    }
}
