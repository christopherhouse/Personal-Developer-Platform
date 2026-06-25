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
        "resource group classified into fabrics, platform, spokes, and workloads, with drift findings and " +
        "subscription coverage. This is DEPLOYED TRUTH (ARG), not recorded intent. Use this to see deployed " +
        "fabrics/spokes/platform; the 'workloads' and 'environments' groups stay empty until workloads exist " +
        "(spec 008).")]
    public async Task<InventorySnapshot> WhatsDeployed(
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        return await inventory.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool, Description(
        "List WORKLOAD ENVIRONMENTS — deployed workloads grouped by their pdp-env tag (e.g. dev, demo), from " +
        "Azure Resource Graph. This is NOT the list of vended spokes/fabrics: those are control-plane managed " +
        "units addressed by env_id — use ShowEnvironment / RunHistory for them. Empty until workloads are " +
        "deployed (spec 008). Deployed truth (ARG), not recorded intent.")]
    public async Task<IReadOnlyList<EnvironmentView>> ListWorkloadEnvironments(
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        return await inventory.GetEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
    }
}
