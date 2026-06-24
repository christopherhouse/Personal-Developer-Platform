using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using Pdp.ControlPlane.Registry;
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

    /// <summary>
    /// Runs a gated mutation (apply/destroy) and translates the verb-layer plan-gate rejections (FR-019)
    /// into clear, retryable <see cref="McpException"/> messages: a plan that has not finished yet, a plan
    /// that failed, and a genuine concurrent operation are surfaced distinctly. The caller invokes this
    /// <b>between</b> <c>tokens.Check</c> and <c>tokens.Consume</c>, so any rejection here leaves the
    /// confirmation token unconsumed (the apply is a pure read+dispatch; it never reconciles or blocks).
    /// </summary>
    protected static async Task<T> InvokeGatedAsync<T>(Func<Task<T>> mutation)
    {
        try
        {
            return await mutation().ConfigureAwait(false);
        }
        catch (PlanNotReadyException)
        {
            throw new McpException(
                "The plan run has not finished yet. Check it with ShowEnvironment or RunStatus, then retry " +
                "once it reports Succeeded — nothing was applied and your confirmation token is still valid.");
        }
        catch (PlanFailedException)
        {
            throw new McpException(
                "The plan run did not succeed, so there is nothing to apply. Re-plan with the matching Plan… " +
                "tool and try again.");
        }
        catch (OperationInProgressException ex)
        {
            // A genuine concurrent mutating operation is already in flight (single-flight, FR-022a).
            throw new McpException(ex.Message);
        }
    }
}
