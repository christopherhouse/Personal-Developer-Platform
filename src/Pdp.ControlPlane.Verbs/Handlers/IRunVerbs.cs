using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The run / registry-audit read verbs (FR-015) — the answer to "what did I ask for and what
/// happened?" (contracts/verb-surface.md §5). They read the intent registry only: an environment's
/// recorded intent + status, its provisioning-run audit trail, and a single run's detail. "What is
/// actually deployed?" is a different question answered by <see cref="IInventoryVerbs"/> over ARG — the
/// division of truth (FR-016). All read verbs stay available even while a run is in flight.
/// </summary>
public interface IRunVerbs
{
    /// <summary>The recorded intent + lifecycle status for one environment, or null if unknown.</summary>
    Task<EnvironmentRecord?> GetEnvironmentAsync(EnvRef environment, CancellationToken cancellationToken = default);

    /// <summary>An environment's provisioning-run audit trail, newest first.</summary>
    Task<IReadOnlyList<RunRecord>> GetRunsAsync(EnvRef environment, CancellationToken cancellationToken = default);

    /// <summary>A single provisioning run by its surrogate <c>run_id</c>, or null if unknown.</summary>
    Task<RunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);
}
