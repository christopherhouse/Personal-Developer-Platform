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
/// <param name="Inputs">The exact <c>workflow_dispatch</c> inputs (env_id, mode, spoke_cidr, …).</param>
/// <param name="Mode">
/// The phase the first run dispatches: <see cref="RunPhase.Plan"/> for the two-phase Article VIII
/// gate (US2 — plan first, then confirm), or <see cref="RunPhase.Apply"/> for the US1 one-shot vend.
/// Either way the environment enters <see cref="EnvironmentStatus.Provisioning"/>.
/// </param>
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
    IReadOnlyDictionary<string, string> Inputs,
    RunPhase Mode = RunPhase.Apply);

/// <summary>
/// Starts a spoke-<b>destroy</b> saga (US2). The verb has validated and confirmed the destroy and
/// resolved the existing environment; the saga's <c>Start</c> flips the environment to
/// <see cref="EnvironmentStatus.Destroying"/>, records the destroy run, and cascades the dispatch in
/// one durable transaction. <paramref name="Mode"/> is <see cref="RunPhase.Plan"/> for the
/// destroy-preview (Article VIII) or <see cref="RunPhase.Destroy"/> for the one-shot confirmed destroy.
/// On a successful destroy run the saga releases the IPAM allocation (FR-009); a failed destroy does
/// not release (FR-011).
/// </summary>
/// <param name="EnvId">The surrogate correlation key of the environment being destroyed.</param>
/// <param name="Subscription">Target subscription id.</param>
/// <param name="Region">The environment's region.</param>
/// <param name="Name">The spoke name (the allocation name to release on success).</param>
/// <param name="Owner">Requesting principal.</param>
/// <param name="RunId">The pre-resolved provisioning-run id (UUIDv7).</param>
/// <param name="WorkflowFile">The dispatch workflow (<c>spoke-destroy.yml</c>).</param>
/// <param name="GitRef">The git ref to dispatch against (default branch).</param>
/// <param name="Inputs">The exact <c>workflow_dispatch</c> inputs (env_id, mode, target, destroy-confirm…).</param>
/// <param name="Mode">The destroy phase to dispatch first (plan preview or destroy).</param>
public sealed record BeginSpokeDestroy(
    Guid EnvId,
    string Subscription,
    string Region,
    string Name,
    string Owner,
    Guid RunId,
    string WorkflowFile,
    string GitRef,
    IReadOnlyDictionary<string, string> Inputs,
    RunPhase Mode = RunPhase.Destroy);

/// <summary>
/// Starts a fabric-provisioning saga (US3). A regional fabric (spec 003) owns no per-spoke address
/// block — the verb has registered the region in the IPAM ledger (idempotent <c>RegisterRegionAsync</c>)
/// and resolved the surrogate <see cref="EnvId"/>/<see cref="RunId"/>; the saga's <c>Start</c> records
/// intent + the dispatch run and cascades the <c>fabric-vend.yml</c> dispatch in one durable transaction.
/// A fabric is identified by its <see cref="Region"/> (the environment <c>Name</c>); there is no
/// <c>SpokeCidr</c>. <paramref name="Mode"/> is <see cref="RunPhase.Plan"/> for the two-phase Article VIII
/// gate or <see cref="RunPhase.Apply"/> for a one-shot vend.
/// </summary>
/// <param name="EnvId">The surrogate correlation key (UUIDv7).</param>
/// <param name="Subscription">The platform subscription hosting the fabric.</param>
/// <param name="Region">The fabric's region — also its natural-key name.</param>
/// <param name="Owner">Requesting principal.</param>
/// <param name="RunId">The pre-resolved provisioning-run id (UUIDv7).</param>
/// <param name="WorkflowFile">The dispatch workflow (<c>fabric-vend.yml</c>).</param>
/// <param name="GitRef">The git ref to dispatch against (default branch).</param>
/// <param name="Inputs">The exact <c>workflow_dispatch</c> inputs (env_id, mode, region, region_index).</param>
/// <param name="Mode">The phase the first run dispatches (plan or apply).</param>
public sealed record BeginFabricProvisioning(
    Guid EnvId,
    string Subscription,
    string Region,
    string Owner,
    Guid RunId,
    string WorkflowFile,
    string GitRef,
    IReadOnlyDictionary<string, string> Inputs,
    RunPhase Mode = RunPhase.Apply);

/// <summary>
/// Starts a fabric-<b>destroy</b> saga (US3). The verb has validated and confirmed the destroy (the
/// owner restated the region) and resolved the existing fabric environment; the saga's <c>Start</c>
/// flips it to <see cref="EnvironmentStatus.Destroying"/>, records the destroy run, and cascades the
/// <c>fabric-destroy.yml</c> dispatch in one durable transaction. A fabric destroy releases <b>no</b>
/// IPAM allocation — the region's hub carve-out is a standing reservation that survives (FR-014).
/// </summary>
/// <param name="EnvId">The surrogate correlation key of the fabric being destroyed.</param>
/// <param name="Subscription">The platform subscription.</param>
/// <param name="Region">The fabric's region (also its natural-key name and the destroy-confirm value).</param>
/// <param name="Owner">Requesting principal.</param>
/// <param name="RunId">The pre-resolved provisioning-run id (UUIDv7).</param>
/// <param name="WorkflowFile">The dispatch workflow (<c>fabric-destroy.yml</c>).</param>
/// <param name="GitRef">The git ref to dispatch against (default branch).</param>
/// <param name="Inputs">The exact <c>workflow_dispatch</c> inputs (env_id, mode, region, destroy-confirm…).</param>
/// <param name="Mode">The destroy phase to dispatch first (plan preview or destroy).</param>
public sealed record BeginFabricDestroy(
    Guid EnvId,
    string Subscription,
    string Region,
    string Owner,
    Guid RunId,
    string WorkflowFile,
    string GitRef,
    IReadOnlyDictionary<string, string> Inputs,
    RunPhase Mode = RunPhase.Destroy);

/// <summary>
/// The dispatch envelope shared by the workload saga messages (spec 008 — contracts/workload-verbs.md).
/// Mirrors the field set of the spoke messages minus <c>SpokeCidr</c>: workloads carve no address
/// space, so there is no IPAM interaction anywhere in their lifecycle. The verb layer builds the
/// workflow <see cref="Inputs"/> (env_id, mode, archetype_path, archetype_ref, parameters_json,
/// pdp_env, …) and pre-resolves <see cref="RunId"/>; workload-specific facts beyond dispatch (spoke,
/// stamped version, parameters) live on the <c>registry.workloads</c> detail row, not here.
/// </summary>
/// <param name="Subscription">Target subscription id (== the containing spoke's).</param>
/// <param name="Region">The spoke's region, copied at deploy.</param>
/// <param name="Name">Workload name (natural-key component; unique per subscription — R4).</param>
/// <param name="Owner">Requesting principal.</param>
/// <param name="RunId">The pre-resolved provisioning-run id (UUIDv7).</param>
/// <param name="WorkflowFile">The dispatch workflow (<c>workload-deploy.yml</c> / <c>workload-destroy.yml</c>).</param>
/// <param name="GitRef">The git ref the <b>workflow definition</b> is dispatched against (default
/// branch — the module content is pinned separately by the <c>archetype_ref</c> input, R7).</param>
/// <param name="Inputs">The exact <c>workflow_dispatch</c> inputs.</param>
/// <param name="Mode">The phase the first run dispatches (plan for the Article VIII gate, or the one-shot apply/destroy).</param>
public sealed record WorkloadDispatchInputs(
    string Subscription,
    string Region,
    string Name,
    string Owner,
    Guid RunId,
    string WorkflowFile,
    string GitRef,
    IReadOnlyDictionary<string, string> Inputs,
    RunPhase Mode);

/// <summary>
/// Starts a workload-<b>deploy</b> saga (spec 008, US1). Identical flow to
/// <see cref="BeginSpokeProvisioning"/> minus the IPAM allocate step — the saga's <c>Start</c> upserts
/// the <c>kind='workload'</c> environment row to <c>Provisioning</c>, records the dispatch run, and
/// cascades the <c>workload-deploy.yml</c> dispatch in one durable transaction.
/// </summary>
/// <param name="EnvId">The surrogate correlation key (UUIDv7).</param>
/// <param name="Inputs">The dispatch envelope.</param>
public sealed record BeginWorkloadDeploy(Guid EnvId, WorkloadDispatchInputs Inputs);

/// <summary>
/// Starts a workload-<b>destroy</b> saga (spec 008, US2). Identical flow to
/// <see cref="BeginSpokeDestroy"/> minus the IPAM release step. The verb has already enforced
/// <c>ConfirmationGuard.RequireMatch</c> and resolved the <b>stamped</b> archetype version into the
/// dispatch inputs (destroy checks out the same tag that was applied — R7); the workflow's
/// <c>destroy-confirm</c> input is the second gate layer (Article VIII).
/// </summary>
/// <param name="EnvId">The surrogate correlation key of the workload being destroyed.</param>
/// <param name="Inputs">The dispatch envelope.</param>
public sealed record BeginWorkloadDestroy(Guid EnvId, WorkloadDispatchInputs Inputs);

/// <summary>
/// The owner's explicit confirmation of a previously surfaced plan (Article VIII / FR-006) — routed to
/// the one saga awaiting confirmation, which dispatches the gated mutation (<see cref="RunPhase.Apply"/>
/// after a create-plan, <see cref="RunPhase.Destroy"/> after a destroy-plan). Ignored if the saga is
/// not in fact awaiting confirmation (idempotent). Carries <see cref="SagaIdentityAttribute"/> on
/// <see cref="EnvId"/> so Wolverine resolves the saga.
/// </summary>
/// <param name="EnvId">The environment whose plan is being confirmed (the saga identity).</param>
/// <param name="RunId">The pre-resolved provisioning-run id for the confirmed mutation (UUIDv7).</param>
/// <param name="WorkflowFile">The dispatch workflow for the confirmed mutation.</param>
/// <param name="GitRef">The git ref to dispatch against.</param>
/// <param name="Mode">The confirmed phase to run (apply or destroy).</param>
/// <param name="Inputs">The exact dispatch inputs for the confirmed mutation.</param>
public sealed record ConfirmationGiven(
    [property: SagaIdentity] Guid EnvId,
    Guid RunId,
    string WorkflowFile,
    string GitRef,
    RunPhase Mode,
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
