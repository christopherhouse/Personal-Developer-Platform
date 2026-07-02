namespace Pdp.ControlPlane.Registry.Entities;

/// <summary>What kind of managed environment a registry row represents (data-model §5).</summary>
public enum EnvironmentKind
{
    /// <summary>A regional hub-and-spoke network fabric (spec 003).</summary>
    Fabric,

    /// <summary>A vended spoke (spec 004).</summary>
    Spoke,

    /// <summary>
    /// A workload deployed from the archetype catalog into a vended spoke (spec 008). Carves no
    /// address space (<c>SpokeCidr</c> stays null); workload-specific facts live in the 1:1
    /// <see cref="WorkloadDetails"/> row.
    /// </summary>
    Workload,
}

/// <summary>
/// Catalog lifecycle of an <see cref="Archetype"/> (spec 008, FR-004). Retired = no new deploys;
/// existing workloads (stamped to a version) are unaffected and remain destroyable.
/// </summary>
public enum ArchetypeStatus
{
    /// <summary>Deployable: new workloads may resolve this archetype.</summary>
    Active,

    /// <summary>No new deploys; stamped workloads live on and can still be destroyed.</summary>
    Retired,
}

/// <summary>The recorded outcome of one catalog sync pass (spec 008, FR-006 audit).</summary>
public enum CatalogSyncOutcome
{
    /// <summary>The projection was updated from the file (all-or-nothing transaction).</summary>
    Applied,

    /// <summary>The file hash matched the last successful sync — nothing to do.</summary>
    NoChange,

    /// <summary>The file was invalid or violated version immutability; the prior projection was kept.</summary>
    Rejected,
}

/// <summary>
/// The lifecycle status of an <see cref="Environment"/> — intent/history, never a deployment-truth
/// claim ("is it really deployed?" routes to inventory/ARG — FR-016). Transitions:
/// <c>Requested → Provisioning → Active | Failed</c> and <c>Destroying → Destroyed | Failed</c>.
/// </summary>
public enum EnvironmentStatus
{
    /// <summary>Intent recorded; the apply has not yet been dispatched.</summary>
    Requested,

    /// <summary>An apply run is in flight.</summary>
    Provisioning,

    /// <summary>The apply run succeeded; the environment is provisioned.</summary>
    Active,

    /// <summary>A destroy run is in flight.</summary>
    Destroying,

    /// <summary>The destroy run succeeded (and, for spokes, the allocation was released).</summary>
    Destroyed,

    /// <summary>The most recent run failed.</summary>
    Failed,
}

/// <summary>The phase of a dispatched run — the <c>mode</c> input sent to the workflow (research §3).</summary>
public enum RunPhase
{
    /// <summary><c>tofu plan</c> only; no state mutation (Article VIII preview).</summary>
    Plan,

    /// <summary>Apply (create) the environment.</summary>
    Apply,

    /// <summary>Destroy the environment.</summary>
    Destroy,
}

/// <summary>The outcome of a dispatched run as the control plane tracks it (data-model §2).</summary>
public enum RunOutcome
{
    /// <summary>The <c>workflow_dispatch</c> was sent; the run has not yet been correlated.</summary>
    Dispatched,

    /// <summary>The run was correlated and is executing.</summary>
    InProgress,

    /// <summary>The run completed successfully.</summary>
    Succeeded,

    /// <summary>The run completed with failure.</summary>
    Failed,

    /// <summary>The run was cancelled.</summary>
    Cancelled,

    /// <summary>The run timed out.</summary>
    TimedOut,
}

/// <summary>Which signal first recorded a run's terminal outcome (SC-006 observability).</summary>
public enum TrackingSource
{
    /// <summary>The <c>workflow_run</c> webhook (primary, host-ready).</summary>
    Webhook,

    /// <summary>The polling reconciler (guarantees completion even when a webhook is missed).</summary>
    Reconciler,
}
