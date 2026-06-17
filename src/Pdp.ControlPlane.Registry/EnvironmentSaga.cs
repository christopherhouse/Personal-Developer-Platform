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
/// This Phase-2 type carries only lifecycle <b>state</b>. The transition handlers
/// (<c>Start(SpokeCreateRequested)</c>, <c>Handle(RunCompleted|RunFailed|ConfirmationGiven)</c>) land
/// with US1/US2 (T033, T043). The inherited <see cref="Saga.Version"/> is the optimistic-concurrency
/// token Wolverine's EF Core saga storage uses so concurrent run signals (webhook vs reconciler)
/// cannot corrupt state.
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
    /// Starts the spoke-provisioning lifecycle (US1). Runs inside Wolverine's EF Core transactional
    /// middleware on <paramref name="db"/>, so the intent write (the <c>environments</c> row, upserted
    /// to <c>Provisioning</c> with the ledger-allocated CIDR), the dispatch-run write (the
    /// <c>provisioning_runs</c> row in <c>Dispatched</c>), the saga state, and the cascaded
    /// <see cref="DispatchWorkflowCommand"/> all commit atomically — the dispatch is sent
    /// <b>iff</b> this transaction commits (the durable outbox; FR-011, research §6).
    /// </summary>
    /// <returns>The apply-dispatch command, cascaded through the outbox after commit.</returns>
    public DispatchWorkflowCommand Start(BeginSpokeProvisioning message, RegistryDbContext db)
    {
        Id = message.EnvId;
        Status = EnvironmentStatus.Provisioning;
        CurrentPhase = RunPhase.Apply;
        CurrentRunId = message.RunId;

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

        db.ProvisioningRuns.Add(new ProvisioningRun
        {
            RunId = message.RunId,
            EnvId = message.EnvId,
            Phase = RunPhase.Apply,
            WorkflowFile = message.WorkflowFile,
            DispatchInputs = SerializeInputs(message.Inputs),
            Outcome = RunOutcome.Dispatched,
            DispatchedAt = now,
        });

        return new DispatchWorkflowCommand(
            message.EnvId,
            message.RunId,
            message.WorkflowFile,
            message.GitRef,
            RunPhase.Apply,
            message.Inputs);
    }

    /// <summary>
    /// A dispatched run completed successfully. For an apply, the environment becomes <c>Active</c> and
    /// the saga retires (data-model §3). Destroy completion (with allocation release) lands with US2.
    /// </summary>
    public void Handle(RunCompleted message, RegistryDbContext db)
    {
        var environment = db.Environments.Find(message.EnvId);

        if (message.Phase == RunPhase.Apply)
        {
            Status = EnvironmentStatus.Active;
            if (environment is not null)
            {
                environment.Status = EnvironmentStatus.Active;
                environment.UpdatedAt = DateTimeOffset.UtcNow;
            }

            CurrentPhase = null;
            CurrentRunId = null;
            MarkCompleted();
        }
    }

    /// <summary>
    /// A dispatched run failed (or was cancelled/timed out). The environment is marked <c>Failed</c>;
    /// on a failed <b>create</b> the provisional allocation is released (FR-011) via a cascaded
    /// <see cref="ReleaseAllocationCommand"/> (the verb layer holds the ledger). The saga retires.
    /// </summary>
    /// <returns>The release command for a failed apply, or null when nothing must be released.</returns>
    public ReleaseAllocationCommand? Handle(RunFailed message, RegistryDbContext db)
    {
        var environment = db.Environments.Find(message.EnvId);

        Status = EnvironmentStatus.Failed;
        if (environment is not null)
        {
            environment.Status = EnvironmentStatus.Failed;
            environment.UpdatedAt = DateTimeOffset.UtcNow;
        }

        ReleaseAllocationCommand? release = null;
        if (message.Phase == RunPhase.Apply && environment is not null)
        {
            // The block was allocated provisionally at vend; a failed create must not leak it (FR-011).
            release = new ReleaseAllocationCommand(environment.Region, environment.Name);
        }

        CurrentPhase = null;
        CurrentRunId = null;
        MarkCompleted();
        return release;
    }

    private static string SerializeInputs(IReadOnlyDictionary<string, string> inputs) =>
        System.Text.Json.JsonSerializer.Serialize(inputs);
}
