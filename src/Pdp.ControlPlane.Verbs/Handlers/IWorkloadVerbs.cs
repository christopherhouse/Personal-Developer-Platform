using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The workload verbs (spec 008, contracts/workload-verbs.md) — deploying catalog archetypes into
/// vended spokes through the spec-006 spine with <b>zero reimplementation</b>: same
/// validate → record intent → (plan → confirm) → apply → track flow, same lifecycle saga (via
/// <c>BeginWorkloadDeploy</c>/<c>BeginWorkloadDestroy</c> — no IPAM steps, workloads carve no address
/// space), same dispatch + <c>env_id</c> correlation. Validation order (R3):
/// shape → catalog (active archetype, newest active version) → JSON schema → spoke exists+Active →
/// intent; nothing is recorded or dispatched before all five pass.
/// </summary>
public interface IWorkloadVerbs
{
    /// <summary>
    /// Dispatches a <c>mode=plan</c> deploy run and surfaces its plan for confirmation
    /// (Article VIII). Resolves and permanently stamps the newest active archetype version (FR-005).
    /// </summary>
    Task<PlanResult> PlanDeployAsync(WorkloadDeployRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deploys a workload: the Article VIII confirmation path when a succeeded deploy plan is
    /// awaiting it (mirrors <see cref="ISpokeVerbs.CreateAsync"/>), otherwise the direct one-shot
    /// apply. Repeat deploys of an existing workload converge when the parameters are identical and
    /// are refused (<c>WorkloadParametersChangedException</c>) when they differ — in-place
    /// reconfiguration is destroy → deploy.
    /// </summary>
    Task<VerbResult> DeployAsync(
        WorkloadDeployRequest request,
        Confirmation confirmation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatches a destroy <c>mode=plan</c> preview for confirmation (Article VIII; US2). The
    /// destroy checks out the workload's <b>stamped</b> archetype tag, never the newest.
    /// </summary>
    Task<PlanResult> PlanDestroyAsync(EnvRef workload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Destroys a workload after the mandatory, unbypassable name-restating confirmation
    /// (<c>ConfirmationGuard.RequireMatch</c> before any dispatch — Article VIII; US2). The spoke and
    /// sibling workloads are untouched; there is no allocation to release.
    /// </summary>
    Task<VerbResult> DestroyAsync(
        EnvRef workload,
        Confirmation confirmation,
        CancellationToken cancellationToken = default);
}
