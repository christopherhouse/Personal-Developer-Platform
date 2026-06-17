namespace Pdp.ControlPlane.Registry.Entities;

/// <summary>
/// One row per <b>dispatched execution-plane run</b> — any phase (plan, apply, destroy) — the
/// audit trail behind "what did I ask for and what happened?" (table
/// <c>registry.provisioning_runs</c>, data-model §2, FR-015). Written at dispatch time inside the
/// same outbox transaction as the intent write, so a committed environment always has its run record
/// (no orphan dispatch — research §6).
/// </summary>
public class ProvisioningRun
{
    /// <summary>Our surrogate id (UUIDv7); primary key.</summary>
    public Guid RunId { get; set; }

    /// <summary>The correlated environment (FK → <c>environments</c>).</summary>
    public Guid EnvId { get; set; }

    /// <summary>The dispatched phase (the <c>mode</c> input — research §3).</summary>
    public RunPhase Phase { get; set; }

    /// <summary>The dispatched workflow file (e.g. <c>spoke-vend.yml</c>, <c>fabric-vend.yml</c>).</summary>
    public required string WorkflowFile { get; set; }

    /// <summary>
    /// The exact inputs sent (<c>env_id</c>, <c>mode</c>, <c>spoke_cidr</c>, subscription, region,
    /// name…) as a JSON document; persisted to a <c>jsonb</c> column.
    /// </summary>
    public required string DispatchInputs { get; set; }

    /// <summary>The Actions run id, resolved by <c>run-name</c> correlation (research §4); null until correlated.</summary>
    public long? GitHubRunId { get; set; }

    /// <summary>Human link to the run; null until correlated.</summary>
    public string? GitHubRunUrl { get; set; }

    /// <summary>The tracked outcome: <c>Dispatched → InProgress → Succeeded | Failed | Cancelled | TimedOut</c>.</summary>
    public RunOutcome Outcome { get; set; }

    /// <summary>Captured plan output/summary surfaced for confirmation (Plan phase only — Article VIII).</summary>
    public string? PlanSummary { get; set; }

    /// <summary>When the <c>workflow_dispatch</c> was sent.</summary>
    public DateTimeOffset? DispatchedAt { get; set; }

    /// <summary>When the run reached a terminal outcome.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Which signal recorded the terminal outcome (webhook or reconciler — SC-006); null until terminal.
    /// </summary>
    public TrackingSource? TrackedBy { get; set; }

    /// <summary>Navigation to the owning environment.</summary>
    public Environment? Environment { get; set; }
}
