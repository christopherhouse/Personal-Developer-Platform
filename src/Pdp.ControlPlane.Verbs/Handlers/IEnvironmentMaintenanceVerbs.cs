using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The environment-maintenance recovery verbs (issue #48) — the single front-end-agnostic surface the
/// <c>pdp</c> CLI and the MCP wrap for unwedging a stuck environment. When a dispatched run dies without
/// ever recording a terminal outcome, its environment stays non-terminal forever and the single-flight
/// guard (FR-022a) then rejects <b>every</b> subsequent mutating verb — including a destroy — so the unit
/// can never be torn down through the sanctioned path. <see cref="ResetAsync"/> is the in-product escape
/// hatch: an Article VIII-gated, owner-only force-terminal that clears the guard and <b>nothing else</b>
/// (no dispatch, no Azure mutation, no IPAM release). Kind-agnostic — a reset is pure registry state, so
/// one verb serves both fabrics and spokes.
/// </summary>
public interface IEnvironmentMaintenanceVerbs
{
    /// <summary>
    /// Previews a reset without mutating anything: resolves the environment, verifies it is genuinely
    /// wedged (non-terminal), and surfaces its status + latest run so the owner can confirm it is stranded
    /// before restating the target (Article VIII). Throws <see cref="EnvironmentNotFoundException"/> for an
    /// unknown ref and <see cref="EnvironmentNotWedgedException"/> when the environment is already terminal.
    /// </summary>
    Task<ResetPreview> PlanResetAsync(EnvRef environment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Force-resets a wedged environment to <c>Failed</c> after an explicit, matching confirmation (the
    /// owner restates the target name; unbypassable — Article VIII / FR-007), releasing the single-flight
    /// guard. Idempotent: if the environment already reached a terminal state (e.g. the reconciler advanced
    /// it between plan and confirm) it is a no-op that reports the current status. Touches registry status
    /// only — the owner tears down the surviving Azure resources / IPAM allocation through the destroy verb.
    /// </summary>
    Task<VerbResult> ResetAsync(
        EnvRef environment,
        Confirmation confirmation,
        CancellationToken cancellationToken = default);
}
