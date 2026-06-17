using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Dispatch;

/// <summary>
/// The outbound execution-plane boundary (Article II / FR-002/FR-003): triggers a
/// <c>workflow_dispatch</c> as the <c>pdp-orchestrator</c> GitHub App. The control plane
/// <b>dispatches</b> here; it never runs OpenTofu in-process. The implementation
/// (<c>GitHubWorkflowDispatcher</c>, T030) authenticates via <see cref="IGitHubAppCredential"/>,
/// wraps outbound calls in <c>Microsoft.Extensions.Http.Resilience</c>, and is enqueued through
/// Wolverine's durable outbox so a dispatch is sent <b>iff</b> the allocate+record transaction
/// commits (no leaked allocation, no orphan dispatch — FR-011).
/// </summary>
public interface IWorkflowDispatcher
{
    /// <summary>
    /// Triggers the <c>workflow_dispatch</c>. A thin Octokit adapter: the <c>ProvisioningRun</c> row is
    /// written transactionally upstream (the saga, inside the durable outbox) and this call is the
    /// cascaded side effect (FR-011), so it returns nothing. The dispatched workflow sets
    /// <c>run-name: pdp &lt;mode&gt; &lt;env_id&gt;</c> so the run can later be correlated back to its
    /// environment (research §4); correlation is by <c>run-name</c>, not by a dispatch-time id (the
    /// GitHub API returns none).
    /// </summary>
    Task DispatchAsync(WorkflowDispatch dispatch, CancellationToken cancellationToken = default);
}

/// <summary>
/// One dispatch request. <see cref="Inputs"/> MUST include <c>env_id</c> and <c>mode</c>; for spoke
/// create it MUST include the ledger-allocated <c>spoke_cidr</c> (Article VI / FR-008) — never a
/// user-supplied value.
/// </summary>
/// <param name="WorkflowFile">e.g. <c>spoke-vend.yml</c>, <c>fabric-vend.yml</c>, <c>spoke-destroy.yml</c>.</param>
/// <param name="GitRef">The git ref to dispatch against (the default branch).</param>
/// <param name="EnvId">The correlation surrogate echoed into the <c>run-name</c>.</param>
/// <param name="Mode">The phase to run — gates <c>tofu plan</c> vs <c>apply</c>/<c>destroy</c> (Article VIII).</param>
/// <param name="Inputs">The exact <c>workflow_dispatch</c> inputs (env_id, mode, spoke_cidr, subscription, region, name…).</param>
public sealed record WorkflowDispatch(
    string WorkflowFile,
    string GitRef,
    Guid EnvId,
    RunPhase Mode,
    IReadOnlyDictionary<string, string> Inputs);
