using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The fabric verbs (spec 003 wrapped) — the single front-end-agnostic surface the <c>pdp</c> CLI and
/// the future MCP wrap (contracts/verb-surface.md §1). A fabric is the regional hub-and-spoke network
/// fabric; it owns no per-spoke address block (no Gate-G1 allocation), but <see cref="CreateAsync"/>
/// registers the region in the IPAM ledger if needed (idempotent <c>RegisterRegionAsync</c>) and
/// dispatches the new <c>fabric-vend.yml</c> (FR-012a). Every mutating verb follows the same Article VIII
/// spine as spokes — <b>validate → register → record intent → (plan → confirm) → apply → track</b> —
/// dispatching workflows, never running OpenTofu in-process (Article II).
/// </summary>
public interface IFabricVerbs
{
    /// <summary>Dispatches a <c>mode=plan</c> run and surfaces its plan for confirmation (Article VIII).</summary>
    Task<PlanResult> PlanCreateAsync(FabricCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stands up a regional fabric: validate → register the region in the ledger if needed → record
    /// intent + the dispatch run atomically → dispatch <c>fabric-vend.yml</c> in <c>mode=apply</c> →
    /// return a <see cref="VerbResult"/> the caller tracks to terminal. Confirms a previously surfaced
    /// plan when one is pending. Throws <c>OperationInProgressException</c> when a run is already in
    /// flight for this fabric (FR-022a).
    /// </summary>
    Task<VerbResult> CreateAsync(
        FabricCreateRequest request,
        Confirmation confirmation,
        CancellationToken cancellationToken = default);

    /// <summary>Dispatches a destroy <c>mode=plan</c> (destroy preview) for confirmation (Article VIII).</summary>
    Task<PlanResult> PlanDestroyAsync(EnvRef environment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Destroys a regional fabric after explicit, matching confirmation (the owner restates the region;
    /// unbypassable — FR-007). Releases no IPAM allocation — the region's hub carve-out survives (FR-014).
    /// </summary>
    Task<VerbResult> DestroyAsync(
        EnvRef environment,
        Confirmation confirmation,
        CancellationToken cancellationToken = default);
}
