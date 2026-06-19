using System.ComponentModel;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Pdp.ControlPlane.Verbs.Handlers;
using Pdp.ControlPlane.Verbs.Model;
using Pdp.Mcp.Auth;

namespace Pdp.Mcp.Tools;

/// <summary>
/// The run / registry-audit read MCP tools — a <b>thin adapter</b> over <see cref="IRunVerbs"/>
/// (contracts/mcp-tool-surface.md §Read; data-model §4). This is the "what did I ask for, and what
/// happened?" side of the <b>division of truth</b> (FR-016): every answer here is read from the intent
/// registry (recorded intent + lifecycle status + the provisioning-run audit trail), <b>never</b> from ARG.
/// Deployed truth is a different question answered by <see cref="InventoryTools"/>. No registry query logic
/// is duplicated — it delegates 1:1 to the spec-006 verbs (SC-003/SC-004).
/// </summary>
[McpServerToolType]
public sealed class RunTools(IRunVerbs runs, IOptions<McpAuthOptions> auth) : OwnerTool(auth)
{
    [McpServerTool, Description(
        "Show one environment's RECORDED INTENT and lifecycle status from the registry (what was asked for, " +
        "not what is currently deployed). The environment is referenced by env_id (UUID) or by " +
        "kind:subscription:name (e.g. spoke:<sub>:app5). Returns null if no such environment is recorded.")]
    public async Task<EnvironmentRecord?> ShowEnvironment(
        [Description("Environment ref: an env_id (UUID) or kind:subscription:name (e.g. spoke:<sub>:app5).")] string environment,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        var target = ParseEnvRef(environment);
        return await runs.GetEnvironmentAsync(target, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool, Description(
        "Show an environment's provisioning-run audit trail (newest first) from the registry — every " +
        "dispatched plan/apply/destroy and its tracked outcome. The environment is referenced by env_id " +
        "(UUID) or kind:subscription:name (e.g. spoke:<sub>:app5).")]
    public async Task<IReadOnlyList<RunRecord>> RunHistory(
        [Description("Environment ref: an env_id (UUID) or kind:subscription:name (e.g. spoke:<sub>:app5).")] string environment,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        var target = ParseEnvRef(environment);
        return await runs.GetRunsAsync(target, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool, Description(
        "Show one provisioning run in full by its run_id (UUID): the dispatched phase, workflow + inputs, the " +
        "correlated GitHub Actions run, captured plan output, and the terminal outcome. Returns null if no " +
        "such run is recorded.")]
    public async Task<RunRecord?> RunStatus(
        [Description("The provisioning run id (UUID).")] string runId,
        ClaimsPrincipal? caller,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(caller);
        if (!Guid.TryParse(runId, out var id))
        {
            throw new McpException($"'{runId}' is not a valid run id (UUID).");
        }

        return await runs.GetRunAsync(id, cancellationToken).ConfigureAwait(false);
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
