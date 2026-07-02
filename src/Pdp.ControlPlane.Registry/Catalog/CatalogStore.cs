using Microsoft.EntityFrameworkCore;
using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Registry.Catalog;

/// <summary>
/// <see cref="ICatalogStore"/> over <see cref="RegistryDbContext"/>. Version ordering is SemVer
/// precedence, computed in memory (<see cref="SemVerComparer"/>) — Postgres text ordering would rank
/// <c>v1.9.0</c> above <c>v1.10.0</c>; catalogs are O(few) rows so loading a version set is free.
/// </summary>
public sealed class CatalogStore(RegistryDbContext context) : ICatalogStore
{
    /// <inheritdoc />
    public Task<Archetype?> FindArchetypeAsync(string name, CancellationToken cancellationToken = default) =>
        context.Archetypes
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.Name == name, cancellationToken);

    /// <inheritdoc />
    public async Task<ArchetypeVersion?> ResolveNewestVersionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var versions = await context.ArchetypeVersions
            .AsNoTracking()
            .Where(v => v.ArchetypeName == name)
            .ToListAsync(cancellationToken);

        return versions
            .OrderByDescending(v => v.Version, SemVerComparer.Instance)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public Task<ArchetypeVersion?> FindVersionAsync(
        string name,
        string version,
        CancellationToken cancellationToken = default) =>
        context.ArchetypeVersions
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.ArchetypeName == name && v.Version == version, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Archetype>> ListAsync(CancellationToken cancellationToken = default)
    {
        var archetypes = await context.Archetypes
            .AsNoTracking()
            .Include(a => a.Versions)
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);

        foreach (var archetype in archetypes)
        {
            archetype.Versions.Sort((x, y) => SemVerComparer.Instance.Compare(y.Version, x.Version));
        }

        return archetypes;
    }
}
