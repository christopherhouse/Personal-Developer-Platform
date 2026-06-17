using System.Net;
using Pdp.ControlPlane.Registry.Entities;
using Wolverine.Persistence.Sagas;

namespace Pdp.ControlPlane.Registry.Lifecycle;

// The messages that drive the environment lifecycle saga (data-model §3). They live in the registry
// assembly — the lowest project both the saga (here) and the verb/dispatch handlers (which reference
// registry) can see — and are plain, serializable POCOs carried through Wolverine's durable
// inbox/outbox. env_id is the correlation key throughout (research §8); on the run-completion events it
// carries [SagaIdentity] so Wolverine routes the event to the one saga instance.

/// <summary>
/// Starts a spoke-provisioning saga (US1). The verb has already validated, allocated the block from
/// the ledger (Gate-G1), and pre-resolved the surrogate <see cref="EnvId"/> and <see cref="RunId"/>;
/// the saga's <c>Start</c> records intent + the dispatch run and cascades the apply dispatch in one
/// durable transaction (research §6).
/// </summary>
/// <param name="EnvId">The surrogate correlation key (UUIDv7).</param>
/// <param name="Subscription">Target subscription id.</param>
/// <param name="Region">Registered region.</param>
/// <param name="Name">Spoke name (natural-key component).</param>
/// <param name="Owner">Requesting principal.</param>
/// <param name="SpokeCidr">The block allocated from the ledger (FR-008) — mirrored on the env row.</param>
/// <param name="RunId">The pre-resolved provisioning-run id (UUIDv7).</param>
/// <param name="WorkflowFile">The dispatch workflow (<c>spoke-vend.yml</c>).</param>
/// <param name="GitRef">The git ref to dispatch against (default branch).</param>
/// <param name="Inputs">The exact <c>workflow_dispatch</c> inputs (env_id, mode=apply, spoke_cidr, …).</param>
public sealed record BeginSpokeProvisioning(
    Guid EnvId,
    string Subscription,
    string Region,
    string Name,
    string Owner,
    IPNetwork SpokeCidr,
    Guid RunId,
    string WorkflowFile,
    string GitRef,
    IReadOnlyDictionary<string, string> Inputs);

/// <summary>
/// Cascaded from the saga inside the same durable transaction as the intent write, then processed by
/// the dispatch handler <b>after</b> commit (the transactional outbox — FR-011): the actual
/// <c>workflow_dispatch</c> GitHub call. Wolverine retries it durably if GitHub is transiently
/// unavailable.
/// </summary>
/// <param name="EnvId">Correlation surrogate (echoed into the workflow <c>run-name</c>).</param>
/// <param name="RunId">The provisioning-run row this dispatch belongs to.</param>
/// <param name="WorkflowFile">The workflow file to dispatch.</param>
/// <param name="GitRef">The git ref to dispatch against.</param>
/// <param name="Mode">The phase to run — gates <c>tofu plan</c> vs <c>apply</c>/<c>destroy</c>.</param>
/// <param name="Inputs">The exact dispatch inputs.</param>
public sealed record DispatchWorkflowCommand(
    Guid EnvId,
    Guid RunId,
    string WorkflowFile,
    string GitRef,
    RunPhase Mode,
    IReadOnlyDictionary<string, string> Inputs);

/// <summary>
/// Cascaded from the saga to release an environment's IPAM allocation (FR-009/FR-011): on a failed
/// <b>create</b> (release the provisional block) and, with US2, on a successful <b>destroy</b>.
/// Handled in the verb layer, which holds the <c>IIpamLedger</c> reference; the registry assembly does
/// not depend on IPAM.
/// </summary>
/// <param name="Region">The region whose pool holds the allocation.</param>
/// <param name="Name">The allocation name (the spoke name).</param>
public sealed record ReleaseAllocationCommand(string Region, string Name);

/// <summary>
/// A dispatched run reached a <b>successful</b> terminal outcome — emitted by the run tracker (from the
/// webhook or the reconciler) once it records the terminal run, and routed to the saga to advance the
/// environment's status (data-model §3). <see cref="EnvId"/> carries <see cref="SagaIdentityAttribute"/>
/// so Wolverine resolves the saga.
/// </summary>
/// <param name="EnvId">The environment whose run completed (the saga identity).</param>
/// <param name="Phase">The phase that completed (plan/apply/destroy).</param>
public sealed record RunCompleted([property: SagaIdentity] Guid EnvId, RunPhase Phase);

/// <summary>
/// A dispatched run reached a <b>non-successful</b> terminal outcome (failed/cancelled/timed-out) —
/// emitted by the run tracker and routed to the saga, which marks the environment <c>Failed</c> and (on
/// a failed create) releases the provisional allocation (FR-011).
/// </summary>
/// <param name="EnvId">The environment whose run failed (the saga identity).</param>
/// <param name="Phase">The phase that failed.</param>
/// <param name="Outcome">The terminal outcome observed.</param>
public sealed record RunFailed([property: SagaIdentity] Guid EnvId, RunPhase Phase, RunOutcome Outcome);
