using System.ComponentModel;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.Mcp.Auth;

namespace Pdp.Mcp.Tools;

/// <summary>
/// The IPAM read MCP tool — a <b>thin adapter</b> over <see cref="IIpamVerbs"/> (contracts/mcp-tool-surface.md
/// §Read; data-model §4). Read-only: it reports region utilization straight from the spec-002 ledger (the
/// single authority for address space — Article VI / FR-016) and reimplements no address logic. Mirrors the
/// CLI <c>pdp ipam query</c>: one region when named, every registered region otherwise.
/// </summary>
[McpServerToolType]
public sealed class IpamTools(IIpamVerbs ipam, IOptions<McpAuthOptions> auth) : OwnerTool(auth)
{
    [McpServerTool, Description(
        "Report IPAM ledger utilization — the supernet, hub carve-out, live allocations, and free space for " +
        "a region. Pass a region to report just that one; omit it to report every registered region. " +
        "Read-only: queries the ledger and changes nothing.")]
    public async Task<IReadOnlyList<RegionView>> QueryIpam(
        ClaimsPrincipal? caller,
        [Description("One registered region (e.g. westus3); omit to report every region.")] string? region = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        if (string.IsNullOrWhiteSpace(region))
        {
            return await ipam.QueryAllAsync(cancellationToken).ConfigureAwait(false);
        }

        return [await ipam.QueryAsync(region, cancellationToken).ConfigureAwait(false)];
    }
}
