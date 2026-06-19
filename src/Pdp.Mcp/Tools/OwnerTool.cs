using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Pdp.Mcp.Auth;

namespace Pdp.Mcp.Tools;

/// <summary>
/// Base for the MCP tool classes: a defense-in-depth owner check at every verb call
/// (contracts/mcp-tool-surface.md §Invariants). The <c>OwnerOnly</c> policy on <c>MapMcp()</c> is the
/// primary gate; <see cref="EnsureOwner"/> re-asserts it from the validated JWT's <c>oid</c> so identity is
/// read <b>only</b> from the token, never from tool arguments (data-model §3/§4). Fail-closed: a missing
/// owner config or a non-owner caller throws before any verb runs.
/// </summary>
public abstract class OwnerTool(IOptions<McpAuthOptions> authOptions)
{
    private readonly string _ownerOid = authOptions.Value.OwnerOid;

    /// <summary>
    /// Throws <see cref="McpException"/> unless <paramref name="caller"/> carries the single allow-listed
    /// owner <c>oid</c>. Called first by every tool method.
    /// </summary>
    protected void EnsureOwner(ClaimsPrincipal? caller)
    {
        var oid = caller?.FindFirst("oid")?.Value;
        if (string.IsNullOrEmpty(_ownerOid) || !string.Equals(oid, _ownerOid, StringComparison.Ordinal))
        {
            throw new McpException("This control plane is restricted to its single platform owner.");
        }
    }
}
