namespace Pdp.ControlPlane.Registry.Entities;

/// <summary>
/// Workload-specific facts, a 1:1 extension of a managed-unit row (table <c>registry.workloads</c>,
/// spec-008 data-model): a workload <b>is</b> an <see cref="Environment"/> with
/// <see cref="EnvironmentKind.Workload"/> — it reuses the status machine, saga, and run audit —
/// while this row carries what only workloads have: the containing spoke, the stamped archetype
/// version (FR-005, never mutated), the <c>pdp-env</c> value, and the schema-validated parameters
/// exactly as dispatched.
/// </summary>
public class WorkloadDetails
{
    /// <summary>The managed-unit row this extends (PK; FK → <c>environments</c>, cascade).</summary>
    public Guid EnvId { get; set; }

    /// <summary>Target subscription of the containing spoke (== the workload's subscription).</summary>
    public required string SpokeSubscription { get; set; }

    /// <summary>The containing spoke — the state-key component and the FR-021 guard join.</summary>
    public required string SpokeName { get; set; }

    /// <summary>The deployed archetype (FK, with <see cref="ArchetypeVersion"/>, → <c>archetype_versions</c>).</summary>
    public required string ArchetypeName { get; set; }

    /// <summary>
    /// The version <b>stamped permanently at deploy</b> (FR-005): destroys check out this tag, and a
    /// newer catalog version never moves it.
    /// </summary>
    public required string ArchetypeVersion { get; set; }

    /// <summary>The workload environment value (<c>pdp-env</c> tag, <c>^[a-z0-9-]{1,16}$</c>).</summary>
    public required string PdpEnv { get; set; }

    /// <summary>The schema-validated caller parameters, as dispatched; <c>jsonb</c>.</summary>
    public required string Parameters { get; set; }

    /// <summary>Audit: when the detail row was recorded.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Navigation to the managed-unit row.</summary>
    public Environment? Environment { get; set; }
}
