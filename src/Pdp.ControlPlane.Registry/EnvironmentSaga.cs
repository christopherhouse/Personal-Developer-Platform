using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.Registry.Lifecycle;
using Wolverine;
using Environment = Pdp.ControlPlane.Registry.Entities.Environment;

namespace Pdp.ControlPlane.Registry;

/// <summary>
/// The durable, event-driven lifecycle engine for one environment (Wolverine saga state, persisted
/// via EF Core saga storage in table <c>registry.environment_saga</c> — research §2, data-model §3).
/// One saga per environment: <see cref="Id"/> == the environment's <c>env_id</c>. The saga's
/// <b>existence + non-terminal status</b> is the single-flight guard (FR-022a).
/// </summary>
/// <remarks>
/// The inherited <see cref="Saga.Version"/> is the optimistic-concurrency token Wolverine's EF Core
/// saga storage uses so concurrent run signals (webhook vs reconciler) cannot corrupt state.
/// US1 wires the one-shot apply spine; US2 adds the two-phase Article VIII gate — a
/// <see cref="RunPhase.Plan"/> first run leaves the saga <see cref="PendingConfirmation"/>, and a
/// <see cref="ConfirmationGiven"/> then dispatches the gated apply/destroy.
/// </remarks>
public class EnvironmentSaga : Saga
{
    /// <summary>Saga id == the environment's <c>env_id</c> (one saga per environment).</summary>
    public Guid Id { get; set; }

    /// <summary>Mirrors the environment's lifecycle status (the saga drives it).</summary>
    public EnvironmentStatus Status { get; set; }

    /// <summary>The phase currently in flight (plan/apply/destroy), or null when idle.</summary>
    public RunPhase? CurrentPhase { get; set; }

    /// <summary>True after a plan run completes, awaiting confirmation (Article VIII).</summary>
    public bool PendingConfirmation { get; set; }

    /// <summary>The <see cref="ProvisioningRun"/> currently in flight, if any.</summary>
    public Guid? CurrentRunId { get; set; }

    /// <summary>
    /// Starts the spoke-provisioning lifecycle. Runs inside Wolverine's EF Core transactional
    /// middleware on <paramref name="db"/>, so the intent write (the <c>environments</c> row, upserted
    /// to <c>Provisioning</c> with the ledger-allocated CIDR), the dispatch-run write (the
    /// <c>provisioning_runs</c> row in <c>Dispatched</c>), the saga state, and the cascaded
    /// <see cref="DispatchWorkflowCommand"/> all commit atomically — the dispatch is sent
    /// <b>iff</b> this transaction commits (the durable outbox; FR-011, research §6). The first run is
    /// dispatched in <see cref="BeginSpokeProvisioning.Mode"/>: <c>Plan</c> for the two-phase Article VIII
    /// gate (US2), or <c>Apply</c> for the US1 one-shot vend.
    /// </summary>
    /// <returns>The first-phase dispatch command, cascaded through the outbox after commit.</returns>
    public DispatchWorkflowCommand Start(BeginSpokeProvisioning message, RegistryDbContext db)
    {
        Id = message.EnvId;
        Status = EnvironmentStatus.Provisioning;
        CurrentPhase = message.Mode;
        CurrentRunId = message.RunId;
        PendingConfirmation = false;

        var now = DateTimeOffset.UtcNow;

        // Upsert the environment intent: a re-create of a terminal (kind, subscription, name) converges
        // on the same env_id (FR-022), so the row may already exist — flip it back to Provisioning.
        var environment = db.Environments.Local.FirstOrDefault(e => e.EnvId == message.EnvId)
            ?? db.Environments.Find(message.EnvId);
        if (environment is null)
        {
            environment = new Environment
            {
                EnvId = message.EnvId,
                Kind = EnvironmentKind.Spoke,
                Subscription = message.Subscription,
                Region = message.Region,
                Name = message.Name,
                Owner = message.Owner,
                CreatedAt = now,
            };
            db.Environments.Add(environment);
        }

        environment.Status = EnvironmentStatus.Provisioning;
        environment.SpokeCidr = message.SpokeCidr;
        environment.Owner = message.Owner;
        environment.UpdatedAt = now;

        AddRun(db, message.RunId, message.EnvId, message.Mode, message.WorkflowFile, message.Inputs, now);

        return new DispatchWorkflowCommand(
            message.EnvId,
            message.RunId,
            message.WorkflowFile,
            message.GitRef,
            message.Mode,
            message.Inputs);
    }

    /// <summary>
    /// Starts the spoke-<b>destroy</b> lifecycle (US2). Flips the environment to <c>Destroying</c>,
    /// records the destroy run, and cascades the dispatch — all atomically (the durable outbox). The
    /// first run is dispatched in <see cref="BeginSpokeDestroy.Mode"/>: <c>Plan</c> for the destroy
    /// preview, or <c>Destroy</c> for the one-shot confirmed destroy. On a successful destroy the
    /// allocation is released (FR-009).
    /// </summary>
    /// <returns>The first-phase destroy dispatch command, cascaded after commit.</returns>
    public DispatchWorkflowCommand Start(BeginSpokeDestroy message, RegistryDbContext db)
    {
        Id = message.EnvId;
        Status = EnvironmentStatus.Destroying;
        CurrentPhase = message.Mode;
        CurrentRunId = message.RunId;
        PendingConfirmation = false;

        var now = DateTimeOffset.UtcNow;

        var environment = db.Environments.Local.FirstOrDefault(e => e.EnvId == message.EnvId)
            ?? db.Environments.Find(message.EnvId);
        if (environment is not null)
        {
            environment.Status = EnvironmentStatus.Destroying;
            environment.UpdatedAt = now;
        }

        AddRun(db, message.RunId, message.EnvId, message.Mode, message.WorkflowFile, message.Inputs, now);

        return new DispatchWorkflowCommand(
            message.EnvId,
            message.RunId,
            message.WorkflowFile,
            message.GitRef,
            message.Mode,
            message.Inputs);
    }

    /// <summary>
    /// Starts the fabric-provisioning lifecycle (US3) — the same atomic intent-write + dispatch as a
    /// spoke create, but a fabric owns no per-spoke block (no <c>SpokeCidr</c>) and is identified by its
    /// region (<see cref="Environment.Name"/> == <see cref="BeginFabricProvisioning.Region"/>). The first
    /// run is dispatched in <see cref="BeginFabricProvisioning.Mode"/> (plan or apply).
    /// </summary>
    /// <returns>The first-phase dispatch command, cascaded through the outbox after commit.</returns>
    public DispatchWorkflowCommand Start(BeginFabricProvisioning message, RegistryDbContext db)
    {
        Id = message.EnvId;
        Status = EnvironmentStatus.Provisioning;
        CurrentPhase = message.Mode;
        CurrentRunId = message.RunId;
        PendingConfirmation = false;

        var now = DateTimeOffset.UtcNow;

        var environment = db.Environments.Local.FirstOrDefault(e => e.EnvId == message.EnvId)
            ?? db.Environments.Find(message.EnvId);
        if (environment is null)
        {
            environment = new Environment
            {
                EnvId = message.EnvId,
                Kind = EnvironmentKind.Fabric,
                Subscription = message.Subscription,
                Region = message.Region,
                Name = message.Region,
                Owner = message.Owner,
                CreatedAt = now,
            };
            db.Environments.Add(environment);
        }

        environment.Status = EnvironmentStatus.Provisioning;
        environment.Owner = message.Owner;
        environment.UpdatedAt = now;

        AddRun(db, message.RunId, message.EnvId, message.Mode, message.WorkflowFile, message.Inputs, now);

        return new DispatchWorkflowCommand(
            message.EnvId,
            message.RunId,
            message.WorkflowFile,
            message.GitRef,
            message.Mode,
            message.Inputs);
    }

    /// <summary>
    /// Starts the fabric-<b>destroy</b> lifecycle (US3). Flips the fabric to <c>Destroying</c>, records
    /// the destroy run, and cascades the dispatch — all atomically. A fabric destroy releases <b>no</b>
    /// IPAM allocation; the region's hub carve-out survives (FR-014).
    /// </summary>
    /// <returns>The first-phase destroy dispatch command, cascaded after commit.</returns>
    public DispatchWorkflowCommand Start(BeginFabricDestroy message, RegistryDbContext db)
    {
        Id = message.EnvId;
        Status = EnvironmentStatus.Destroying;
        CurrentPhase = message.Mode;
        CurrentRunId = message.RunId;
        PendingConfirmation = false;

        var now = DateTimeOffset.UtcNow;

        var environment = db.Environments.Local.FirstOrDefault(e => e.EnvId == message.EnvId)
            ?? db.Environments.Find(message.EnvId);
        if (environment is not null)
        {
            environment.Status = EnvironmentStatus.Destroying;
            environment.UpdatedAt = now;
        }

        AddRun(db, message.RunId, message.EnvId, message.Mode, message.WorkflowFile, message.Inputs, now);

        return new DispatchWorkflowCommand(
            message.EnvId,
            message.RunId,
            message.WorkflowFile,
            message.GitRef,
            message.Mode,
            message.Inputs);
    }

    /// <summary>
    /// The owner confirmed a surfaced plan (Article VIII / FR-006). If the saga is in fact awaiting
    /// confirmation, records the gated mutation run and cascades its dispatch (apply after a
    /// create-plan, destroy after a destroy-plan); otherwise it is an idempotent no-op (a late/duplicate
    /// confirmation). The environment status is unchanged here — it is already <c>Provisioning</c>
    /// (create) or <c>Destroying</c> (destroy); the run outcome drives the terminal transition.
    /// </summary>
    /// <returns>The confirmed-mutation dispatch command, or null when nothing was awaiting confirmation.</returns>
    public DispatchWorkflowCommand? Handle(ConfirmationGiven message, RegistryDbContext db)
    {
        // Only a saga still in its plan phase can be confirmed. This is robust against the brief window
        // before RunCompleted{Plan} is processed (CurrentPhase is Plan from Start onward) AND idempotent
        // against a duplicate confirmation (once confirmed, CurrentPhase is Apply/Destroy → no-op).
        if (CurrentPhase != RunPhase.Plan)
        {
            return null;
        }

        PendingConfirmation = false;
        CurrentPhase = message.Mode;
        CurrentRunId = message.RunId;

        AddRun(db, message.RunId, message.EnvId, message.Mode, message.WorkflowFile, message.Inputs, DateTimeOffset.UtcNow);

        return new DispatchWorkflowCommand(
            message.EnvId,
            message.RunId,
            message.WorkflowFile,
            message.GitRef,
            message.Mode,
            message.Inputs);
    }

    /// <summary>
    /// A dispatched run completed successfully (data-model §3):
    /// <list type="bullet">
    /// <item><c>Plan</c> → the saga parks in <see cref="PendingConfirmation"/> (the plan is surfaced for
    /// confirmation); the environment stays non-terminal and the saga lives on.</item>
    /// <item><c>Apply</c> → the environment becomes <c>Active</c> and the saga retires.</item>
    /// <item><c>Destroy</c> → the environment becomes <c>Destroyed</c>, the IPAM allocation is released
    /// (FR-009) via a cascaded <see cref="ReleaseAllocationCommand"/>, and the saga retires.</item>
    /// </list>
    /// </summary>
    /// <returns>The release command for a successful destroy, or null otherwise.</returns>
    public ReleaseAllocationCommand? Handle(RunCompleted message, RegistryDbContext db)
    {
        var environment = db.Environments.Find(message.EnvId);

        switch (message.Phase)
        {
            case RunPhase.Plan:
                // Two-phase Article VIII gate: the plan ran; await the owner's confirmation. The
                // environment stays Provisioning/Destroying and the saga is NOT retired.
                PendingConfirmation = true;
                return null;

            case RunPhase.Apply:
                Status = EnvironmentStatus.Active;
                if (environment is not null)
                {
                    environment.Status = EnvironmentStatus.Active;
                    environment.UpdatedAt = DateTimeOffset.UtcNow;
                }

                CurrentPhase = null;
                CurrentRunId = null;
                MarkCompleted();
                return null;

            case RunPhase.Destroy:
                Status = EnvironmentStatus.Destroyed;
                ReleaseAllocationCommand? release = null;
                if (environment is not null)
                {
                    environment.Status = EnvironmentStatus.Destroyed;
                    environment.UpdatedAt = DateTimeOffset.UtcNow;
                    // A successful SPOKE destroy releases the block, completing spec 004's deferred
                    // teardown. A fabric owns no releasable allocation — its hub carve-out is a standing
                    // reservation that survives the fabric (FR-014), so a fabric destroy releases nothing.
                    if (environment.Kind == EnvironmentKind.Spoke)
                    {
                        release = new ReleaseAllocationCommand(environment.Region, environment.Name);
                    }
                }

                CurrentPhase = null;
                CurrentRunId = null;
                MarkCompleted();
                return release;

            default:
                return null;
        }
    }

    /// <summary>
    /// A dispatched run failed (or was cancelled/timed out). The environment is marked <c>Failed</c>;
    /// a failed <b>create</b> (plan or apply — the block was allocated provisionally at vend) releases
    /// the allocation so nothing leaks (FR-011), while a failed <b>destroy</b> does <b>not</b> release —
    /// the block is still in use until a destroy actually succeeds (FR-011/FR-025). The release is a
    /// cascaded <see cref="ReleaseAllocationCommand"/> (the verb layer holds the ledger). The saga retires.
    /// </summary>
    /// <returns>The release command for a failed create, or null when nothing must be released.</returns>
    public ReleaseAllocationCommand? Handle(RunFailed message, RegistryDbContext db)
    {
        var environment = db.Environments.Find(message.EnvId);

        // The lifecycle is distinguished by the pre-failure status: Provisioning = a create in flight
        // (its block was allocated at vend → release on failure); Destroying = a destroy (keep the block).
        var wasCreate = environment?.Status == EnvironmentStatus.Provisioning;

        Status = EnvironmentStatus.Failed;
        if (environment is not null)
        {
            environment.Status = EnvironmentStatus.Failed;
            environment.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Only a spoke holds a provisional block to release; a fabric create allocates none (FR-014).
        var release = wasCreate && environment is { Kind: EnvironmentKind.Spoke }
            ? new ReleaseAllocationCommand(environment.Region, environment.Name)
            : null;

        CurrentPhase = null;
        CurrentRunId = null;
        MarkCompleted();
        return release;
    }

    /// <summary>Records a dispatched run row (<c>Dispatched</c>) inside the saga's transaction.</summary>
    private static void AddRun(
        RegistryDbContext db,
        Guid runId,
        Guid envId,
        RunPhase phase,
        string workflowFile,
        IReadOnlyDictionary<string, string> inputs,
        DateTimeOffset now) =>
        db.ProvisioningRuns.Add(new ProvisioningRun
        {
            RunId = runId,
            EnvId = envId,
            Phase = phase,
            WorkflowFile = workflowFile,
            DispatchInputs = SerializeInputs(inputs),
            Outcome = RunOutcome.Dispatched,
            DispatchedAt = now,
        });

    private static string SerializeInputs(IReadOnlyDictionary<string, string> inputs) =>
        System.Text.Json.JsonSerializer.Serialize(inputs);
}
