namespace Pdp.ControlPlane.Registry.Entities;

/// <summary>
/// The audit record of one catalog sync pass (table <c>registry.catalog_syncs</c>, spec-008
/// data-model, FR-006): together with the git history of <c>archetypes/catalog.json</c> this is the
/// complete "who changed the catalog, when, and what happened" trail.
/// </summary>
public class CatalogSync
{
    /// <summary>Surrogate id (UUIDv7); primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the whole catalog file as read (the no-change short-circuit key).</summary>
    public required string ContentHash { get; set; }

    /// <summary>Applied, no-change, or rejected (invalid file / immutability violation).</summary>
    public CatalogSyncOutcome Outcome { get; set; }

    /// <summary>
    /// Per-entry actions (added / version-added / retired / reactivated / rejected + reason) as a
    /// JSON document; persisted to a <c>jsonb</c> column.
    /// </summary>
    public required string Summary { get; set; }

    /// <summary>When the sync pass ran.</summary>
    public DateTimeOffset AppliedAt { get; set; }
}
