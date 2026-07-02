using Pdp.ControlPlane.Registry.Entities;
using Environment = Pdp.ControlPlane.Registry.Entities.Environment;

namespace Pdp.ControlPlane.Registry;

/// <summary>
/// The intent registry's operation surface (data-model §1, FR-014/FR-022/FR-022a). Owns the
/// <c>environments</c> rows: the idempotent natural-key claim that backs <b>convergent re-create</b>
/// (FR-022), the <b>single-flight guard</b> (FR-022a), lifecycle status transitions, and the audit
/// reads. It records <b>intent and history only</b> — "what is actually deployed?" is answered by the
/// spec-005 inventory over Azure Resource Graph (FR-016), never here.
/// </summary>
public interface IEnvironmentRegistry
{
    /// <summary>
    /// Claims an environment for a create, returning its surrogate <c>env_id</c>. Idempotent on the
    /// natural key <c>(kind, subscription, name)</c>: a re-create of a <b>terminal</b> environment
    /// converges on the existing <c>env_id</c> (FR-022) and resets it to <see cref="EnvironmentStatus.Requested"/>;
    /// a brand-new environment is created in <see cref="EnvironmentStatus.Requested"/> with a fresh
    /// UUIDv7. A claim against a <b>non-terminal</b> environment is the single-flight rejection.
    /// </summary>
    /// <exception cref="OperationInProgressException">The environment has an operation in flight (FR-022a).</exception>
    Task<Guid> BeginCreateAsync(
        EnvironmentKind kind,
        string subscription,
        string region,
        string name,
        string owner,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compensates a claim that never reached dispatch (e.g. allocation failed, or the start
    /// transaction rolled back): removes a freshly-claimed row that has no provisioning runs, or marks
    /// one with prior history <see cref="EnvironmentStatus.Failed"/>. Leaves no orphan (FR-023).
    /// </summary>
    Task AbortCreateAsync(Guid envId, CancellationToken cancellationToken = default);

    /// <summary>Transitions an environment to <paramref name="status"/> and stamps <c>UpdatedAt</c>.</summary>
    Task TransitionAsync(Guid envId, EnvironmentStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Recovery escape hatch: forces a <b>wedged non-terminal</b> environment to
    /// <see cref="EnvironmentStatus.Failed"/>, releasing the single-flight guard (FR-022a) when its run
    /// died without ever recording a terminal outcome (issue #48). Also retires the environment's wedged
    /// Wolverine saga row in the same transaction (it never got its terminal signal, so it survives and
    /// would otherwise collide when the next destroy/create starts a fresh saga on the same env_id).
    /// Touches <b>registry state only</b> — it dispatches nothing, mutates no Azure resource, and releases
    /// no IPAM allocation; the owner then re-plans a destroy/create through the normal verb path.
    /// Idempotent and safe: a no-op that returns <see langword="null"/> on an already-terminal or unknown
    /// environment. Returns the prior non-terminal status when it reset one.
    /// </summary>
    Task<EnvironmentStatus?> ForceTerminalAsync(Guid envId, CancellationToken cancellationToken = default);

    /// <summary>Resolves an environment by its surrogate <c>env_id</c>, or null if unknown.</summary>
    Task<Environment?> FindByIdAsync(Guid envId, CancellationToken cancellationToken = default);

    /// <summary>Resolves an environment by its natural key <c>(kind, subscription, name)</c>, or null.</summary>
    Task<Environment?> FindByNaturalKeyAsync(
        EnvironmentKind kind,
        string subscription,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Returns an environment's provisioning-run audit trail, newest first (FR-015).</summary>
    Task<IReadOnlyList<ProvisioningRun>> GetRunsAsync(Guid envId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the single most-recent provisioning run for the environment, or <see langword="null"/> if
    /// none exists. Issues a <c>LIMIT 1</c> query — prefer this over <see cref="GetRunsAsync"/> when
    /// only the latest run is needed (FR-015).
    /// </summary>
    Task<ProvisioningRun?> GetLatestRunAsync(Guid envId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a single provisioning run by its surrogate <c>run_id</c>, or null (FR-015).</summary>
    Task<ProvisioningRun?> FindRunByIdAsync(Guid runId, CancellationToken cancellationToken = default);
}
