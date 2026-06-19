using System.ComponentModel;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Pdp.ControlPlane.Inventory.Model;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.Mcp.Auth;

namespace Pdp.Mcp.Tools;

/// <summary>
/// The inventory read MCP tools — a <b>thin adapter</b> over <see cref="IInventoryVerbs"/>
/// (contracts/mcp-tool-surface.md §Read; data-model §4). This is the "what's deployed?" side of the
/// <b>division of truth</b> (FR-016): every answer here is computed live from Azure Resource Graph over the
/// <c>pdp-*</c> tag schema (Article III), <b>never</b> the intent registry. No inventory logic is duplicated
/// — it delegates 1:1 to the spec-005 verbs (SC-003/SC-004).
/// </summary>
[McpServerToolType]
public sealed class InventoryTools(IInventoryVerbs inventory, IOptions<McpAuthOptions> auth) : OwnerTool(auth)
{
    [McpServerTool, Description(
        "What's actually deployed? The full live inventory snapshot from Azure Resource Graph — every managed " +
        "resource group classified and grouped (fabrics, platform, spokes, workloads) with drift findings and " +
        "subscription coverage. This is deployed truth (ARG), not recorded intent.")]
    public async Task<InventorySnapshot> WhatsDeployed(
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        return await inventory.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool, Description(
        "List the deployed environments (workloads grouped by their pdp-env tag), from Azure Resource Graph. " +
        "Deployed truth (ARG), not recorded intent.")]
    public async Task<IReadOnlyList<EnvironmentView>> ListEnvironments(
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        return await inventory.GetEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
    }
}
