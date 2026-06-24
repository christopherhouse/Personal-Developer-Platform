using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// The inbound completion boundary (FR-004/FR-005, SC-006): correlates a <c>workflow_run</c> signal
/// — from the webhook <b>or</b> the polling reconciler — back to an <c>env_id</c> and records the
/// terminal outcome. Recording is idempotent (first-terminal-wins, deduped by
/// <c>(EnvId, GitHubRunId)</c>); on terminal it emits <c>RunCompleted</c>/<c>RunFailed</c> to the
/// lifecycle saga. Implemented by <c>RunTracker</c>/<c>RunReconciler</c> (T031/T032).
/// </summary>
public interface IRunTracker
{
    /// <summary>
    /// Records a run's status. <paramref name="status"/>.<see cref="WorkflowRunStatus.RunName"/> MUST
    /// embed <c>env_id</c> and <c>mode</c> (<c>pdp &lt;mode&gt; &lt;env_id&gt;</c>); unparseable or
    /// foreign runs are ignored. Duplicate/late deliveries MUST NOT corrupt the recorded outcome
    /// (FR-005); <paramref name="source"/> is recorded as <c>TrackedBy</c> (SC-006).
    /// </summary>
    Task RecordRunStatusAsync(
        WorkflowRunStatus status,
        TrackingSource source,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciler sweep: lists environments with a non-terminal in-flight run, queries the GitHub
    /// Actions API for each run's current status, and drives terminal ones to a recorded outcome.
    /// Returns the number of runs advanced. A missed-webhook run reaches terminal within ~2 min (SC-006).
    /// </summary>
    Task<int> ReconcileInFlightAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <b>On-demand, targeted</b> reconcile of a single environment's in-flight run (FR-020): the same
    /// correlate-by-run-name → query GitHub → record-terminal logic as the sweep, but scoped to
    /// <paramref name="envId"/>, so a conversational status read advances <i>its</i> run without sweeping
    /// every in-flight environment. A no-op (returns 0) if the environment is unknown or already terminal.
    /// Idempotent (first-terminal-wins), so it is safe to run concurrently with the background sweep —
    /// this is what lets the stateless, scale-to-zero MCP node observe completion without the Api node.
    /// </summary>
    Task<int> ReconcileEnvironmentAsync(Guid envId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A correlated <c>workflow_run</c> observation. <see cref="RunName"/> carries
/// <c>pdp &lt;mode&gt; &lt;env_id&gt;</c> (research §4); the tracker parses it to resolve the
/// <see cref="ProvisioningRun"/>/<see cref="Environment"/>.
/// </summary>
/// <param name="GitHubRunId">The Actions run id.</param>
/// <param name="RunName">The run's display title (<c>pdp &lt;mode&gt; &lt;env_id&gt;</c>).</param>
/// <param name="Url">Human link to the run.</param>
/// <param name="Outcome">The observed outcome.</param>
public sealed record WorkflowRunStatus(
    long GitHubRunId,
    string RunName,
    string Url,
    RunOutcome Outcome);
