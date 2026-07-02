using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Registry.Catalog;

/// <summary>
/// Read-side access to the catalog projection (spec 008). The verb layer resolves what to deploy
/// through this seam — validation order (R3) checks the archetype exists and is
/// <see cref="ArchetypeStatus.Active"/>, then resolves the <b>newest</b> version, all before any
/// intent is recorded. Writes happen only in <c>CatalogSyncService</c> (api startup); this interface
/// exposes none.
/// </summary>
public interface ICatalogStore
{
    /// <summary>
    /// Finds an archetype by catalog name, or null when unknown. The caller distinguishes unknown
    /// (null) from retired (<see cref="Archetype.Status"/>) — the refusal messages differ (US4-AS2).
    /// </summary>
    Task<Archetype?> FindArchetypeAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the newest version (SemVer precedence) of an archetype — the version a new deploy
    /// stamps (FR-005). Null when the archetype is unknown. Status is <b>not</b> checked here; check
    /// <see cref="FindArchetypeAsync"/> first (v1 has no per-version status).
    /// </summary>
    Task<ArchetypeVersion?> ResolveNewestVersionAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up an exact <c>(archetype, version)</c> — the stamped-version path a destroy uses to
    /// check out the same tag that was applied.
    /// </summary>
    Task<ArchetypeVersion?> FindVersionAsync(string name, string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the whole catalog for display (CLI/MCP), versions included, newest first per archetype.
    /// </summary>
    Task<IReadOnlyList<Archetype>> ListAsync(CancellationToken cancellationToken = default);
}
