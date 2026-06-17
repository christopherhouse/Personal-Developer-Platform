using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The spoke verbs (spec 004 wrapped; Gate-G1 closed) — the single implementation the <c>pdp</c> CLI
/// and the future MCP wrap (contracts/verb-surface.md §2). Every mutating verb follows the spine
/// <b>validate → allocate → record intent → (plan → confirm) → apply → track</b> (Articles II/VI/VIII).
/// The <c>Plan*</c> two-phase methods and <see cref="DestroyAsync"/> land with US2; US1 implements the
/// one-command apply path (<see cref="CreateAsync"/>).
/// </summary>
public interface ISpokeVerbs
{
    /// <summary>
    /// Dispatches a <c>mode=plan</c> run and surfaces its plan for confirmation (Article VIII; US2).
    /// </summary>
    Task<PlanResult> PlanCreateAsync(SpokeCreateRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Vends a spoke end to end: validate → allocate the block live by size from the IPAM ledger
    /// (Gate-G1; FR-008) → record intent + the dispatch run atomically → dispatch <c>spoke-vend.yml</c>
    /// in <c>mode=apply</c> → return a <see cref="VerbResult"/> the caller tracks to terminal. There is
    /// <b>no CIDR input</b>. Throws <c>OperationInProgressException</c> if a run is already in flight for
    /// this environment (FR-022a); fails fast (no leaked allocation, no orphan row) on an invalid
    /// region / no fabric / exhausted address space (FR-023).
    /// </summary>
    Task<VerbResult> CreateAsync(
        SpokeCreateRequest request,
        Confirmation confirmation,
        CancellationToken cancellationToken = default);

    /// <summary>Dispatches a destroy <c>mode=plan</c> (destroy preview) for confirmation (US2).</summary>
    Task<PlanResult> PlanDestroyAsync(EnvRef environment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Destroys a spoke after explicit confirmation and releases its allocation on success (US2).
    /// </summary>
    Task<VerbResult> DestroyAsync(
        EnvRef environment,
        Confirmation confirmation,
        CancellationToken cancellationToken = default);
}
