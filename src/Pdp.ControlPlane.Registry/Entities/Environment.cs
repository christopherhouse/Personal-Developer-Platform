using System.Net;

namespace Pdp.ControlPlane.Registry.Entities;

/// <summary>
/// The recorded <b>intent</b> for one managed environment (table <c>registry.environments</c>,
/// data-model §1, FR-014). This is intent + lifecycle history — <b>not</b> a deployment-truth claim;
/// "what is actually deployed?" is answered by the spec-005 inventory over Azure Resource Graph
/// (FR-016), never from here.
/// </summary>
public class Environment
{
    /// <summary>
    /// The surrogate correlation key (UUIDv7) passed to and from dispatched workflows (research §8).
    /// Immutable once assigned; primary key.
    /// </summary>
    public Guid EnvId { get; set; }

    /// <summary>Fabric, spoke, or workload.</summary>
    public EnvironmentKind Kind { get; set; }

    /// <summary>Target subscription id (Azure GUID). For a fabric, the platform subscription.</summary>
    public required string Subscription { get; set; }

    /// <summary>Registered region (e.g. <c>westus3</c>).</summary>
    public required string Region { get; set; }

    /// <summary>Spoke or workload name; for a fabric, the region (a fabric is identified by its region).</summary>
    public required string Name { get; set; }

    /// <summary>The requesting principal (owner identity; single-owner platform).</summary>
    public required string Owner { get; set; }

    /// <summary>Lifecycle status (intent/history — FR-016).</summary>
    public EnvironmentStatus Status { get; set; }

    /// <summary>
    /// The block allocated from the IPAM ledger at vend (spoke only; null for fabric). Mirrors the
    /// ledger allocation for reporting (FR-008) — the ledger remains the authority (Article VI); this
    /// is never a second source of address truth.
    /// </summary>
    public IPNetwork? SpokeCidr { get; set; }

    /// <summary>Audit: when the intent was first recorded.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Audit: when the row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The provisioning runs dispatched for this environment (audit trail).</summary>
    public List<ProvisioningRun> Runs { get; } = [];
}
