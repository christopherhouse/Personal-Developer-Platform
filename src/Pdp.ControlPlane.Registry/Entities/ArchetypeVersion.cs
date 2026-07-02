namespace Pdp.ControlPlane.Registry.Entities;

/// <summary>
/// One <b>immutable</b> archetype version (table <c>registry.archetype_versions</c>, spec-008
/// data-model). Versions are append-only: once synced, a catalog file presenting different content
/// for an existing <c>(archetype, version)</c> fails the whole sync (R2 — the
/// <see cref="ContentHash"/> check), so a stamped workload's tag can never drift (FR-005).
/// </summary>
public class ArchetypeVersion
{
    /// <summary>The owning archetype (composite-PK part; FK → <c>archetypes</c>).</summary>
    public required string ArchetypeName { get; set; }

    /// <summary>
    /// SemVer with <c>v</c> prefix (e.g. <c>v1.0.0</c>) — the git tag suffix; the full ref is
    /// <c>archetype/&lt;name&gt;/&lt;version&gt;</c>. Composite-PK part.
    /// </summary>
    public required string Version { get; set; }

    /// <summary>Repo-relative OpenTofu module dir (e.g. <c>archetypes/container-app-sql</c>).</summary>
    public required string ModulePath { get; set; }

    /// <summary>The version's JSON Schema (draft 2020-12) for caller parameters; <c>jsonb</c>.</summary>
    public required string ParameterSchema { get; set; }

    /// <summary>
    /// SHA-256 over <c>(module_path, canonical parameter_schema)</c> — the immutability fingerprint
    /// the sync enforces (R2).
    /// </summary>
    public required string ContentHash { get; set; }

    /// <summary>Audit: the sync that introduced this version.</summary>
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>Navigation to the owning archetype.</summary>
    public Archetype? Archetype { get; set; }
}
