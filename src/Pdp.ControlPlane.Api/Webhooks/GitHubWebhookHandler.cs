using Octokit.Webhooks;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Events.WorkflowRun;
using Pdp.ControlPlane.Dispatch;
using Pdp.ControlPlane.Registry.Entities;
using Wolverine;

namespace Pdp.ControlPlane.Api.Webhooks;

/// <summary>
/// The internal <c>workflow_run</c> webhook handler (US5, FR-005). Built on
/// <c>Octokit.Webhooks.AspNetCore</c>: <c>MapGitHubWebhooks</c> validates the HMAC-SHA256 signature
/// (<c>X-Hub-Signature-256</c>) against the configured secret and deserializes the typed payload before
/// this processor runs — so a forged or unsigned delivery never reaches here (defense in depth;
/// contracts/dispatch-and-tracking.md §4). On a <b>completed</b> run we normalise the outcome and hand it
/// to the durable inbox (<see cref="RecordWorkflowRun"/>); the <see cref="RunTracker"/> owns correlation,
/// idempotency, and the lifecycle emit. The handler itself terminates no business logic and never runs
/// OpenTofu (Article II). The low-latency primary path; the polling reconciler (US1) is the safety net.
/// </summary>
public sealed class GitHubWebhookHandler(IMessageBus bus) : WebhookEventProcessor
{
    /// <inheritdoc />
    protected override async ValueTask ProcessWorkflowRunWebhookAsync(
        WebhookHeaders headers,
        WorkflowRunEvent workflowRunEvent,
        WorkflowRunAction action,
        CancellationToken cancellationToken = default)
    {
        // We record terminal outcomes only: an in_progress/requested delivery carries no outcome, and the
        // reconciler already resolves correlation for in-flight runs. Anything but "completed" is ignored.
        if (action != WorkflowRunAction.Completed)
        {
            return;
        }

        var run = workflowRunEvent.WorkflowRun;
        if (run is null)
        {
            return;
        }

        var status = new WorkflowRunStatus(
            run.Id,
            run.Name,
            run.HtmlUrl,
            MapConclusion(run.Conclusion?.StringValue));

        // Persist to the durable inbox, then return — the HTTP 200 acknowledges receipt while the record
        // (and any retries) happen out of band. Idempotent at the tracker, so redeliveries are no-ops.
        await bus.PublishAsync(new RecordWorkflowRun(status, TrackingSource.Webhook)).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps GitHub's <c>workflow_run.conclusion</c> string to our terminal <see cref="RunOutcome"/>
    /// (mirrors <c>RunTracker</c>'s Actions-API mapping): anything that is not a clean success/cancel/
    /// timeout — failure, neutral, action_required, stale, skipped, or a null conclusion — is a failed
    /// terminal for our purposes.
    /// </summary>
    private static RunOutcome MapConclusion(string? conclusion) => conclusion switch
    {
        "success" => RunOutcome.Succeeded,
        "cancelled" => RunOutcome.Cancelled,
        "timed_out" => RunOutcome.TimedOut,
        _ => RunOutcome.Failed,
    };
}
