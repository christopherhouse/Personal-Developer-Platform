using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// Carries a correlated <c>workflow_run</c> observation through Wolverine's <b>durable inbox</b> to the
/// run tracker (US5). The webhook endpoint validates the HMAC, parses the typed payload, and publishes
/// this message — so the delivery is persisted and acknowledged with HTTP 200 before
/// <see cref="IRunTracker.RecordRunStatusAsync"/> runs; a crash mid-record replays it rather than losing
/// it (contracts/dispatch-and-tracking.md §4). Recording is idempotent (first-terminal-wins, deduped by
/// <c>(EnvId, GitHubRunId)</c>), so a redelivered webbook is a no-op.
/// </summary>
/// <param name="Status">The observed run status (run-name carries <c>pdp &lt;mode&gt; &lt;env_id&gt;</c>).</param>
/// <param name="Source">Which signal observed it — <see cref="TrackingSource.Webhook"/> for this path.</param>
public sealed record RecordWorkflowRun(WorkflowRunStatus Status, TrackingSource Source);

/// <summary>
/// Drains <see cref="RecordWorkflowRun"/> from the durable inbox into
/// <see cref="IRunTracker.RecordRunStatusAsync"/>. Kept trivial on purpose: the webhook handler does no
/// business logic (defense in depth — §4); the tracker owns correlation, idempotency, and the lifecycle
/// emit. Wolverine retries durably if the record transiently fails.
/// </summary>
public static class RunTrackingHandler
{
    /// <summary>Records the inboxed run status via the tracker.</summary>
    public static Task Handle(
        RecordWorkflowRun message,
        IRunTracker tracker,
        CancellationToken cancellationToken) =>
        tracker.RecordRunStatusAsync(message.Status, message.Source, cancellationToken);
}
