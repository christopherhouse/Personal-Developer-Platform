namespace Pdp.ControlPlane.Registry.Entities;

/// <summary>
/// One catalog archetype (table <c>registry.archetypes</c>, spec-008 data-model). This is a
/// <b>projection</b> of the repo-managed <c>archetypes/catalog.json</c> — the file (changed only by
/// PR) is the source of deployable truth; <c>CatalogSyncService</c> maintains these rows at api
/// startup and nothing else writes them (chat/CLI can never alter the catalog).
/// </summary>
public class Archetype
{
    /// <summary>Catalog identity (<c>^[a-z0-9-]{1,32}$</c>); primary key.</summary>
    public required string Name { get; set; }

    /// <summary>Human summary, shown when the CLI/MCP lists deployable archetypes.</summary>
    public required string Description { get; set; }

    /// <summary>Active (deployable) or retired (no new deploys — FR-004).</summary>
    public ArchetypeStatus Status { get; set; }

    /// <summary>Audit: when the first sync introduced this archetype.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Audit: when a sync last changed this row (status/description).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The registered versions (append-only, content-immutable — R2).</summary>
    public List<ArchetypeVersion> Versions { get; } = [];
}
