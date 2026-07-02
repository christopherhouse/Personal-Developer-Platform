using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pdp.ControlPlane.Registry.Catalog;

/// <summary>
/// Startup projection of the baked-in <c>archetypes/catalog.json</c> into the registry (spec 008,
/// R1). Hosted by <b>Pdp.ControlPlane.Api only</b> — the api app is the catalog's sole writer; the
/// MCP app reads the projected tables and never syncs (no dual writers). Runs to completion inside
/// <see cref="StartAsync"/> so the projection is current before the host serves verbs. A failed sync
/// never blocks boot: the previous projection keeps serving (contracts/archetype-catalog.md).
/// </summary>
public sealed class CatalogSyncService(
    IServiceScopeFactory scopeFactory,
    IOptions<CatalogSyncOptions> options,
    ILogger<CatalogSynchronizer> synchronizerLogger,
    ILogger<CatalogSyncService> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var path = ResolveCatalogPath(options.Value.CatalogPath);
        if (path is null)
        {
            // In the image the Dockerfile COPY guarantees the file; missing here means a local host
            // launched outside the repo. Loud, but not fatal — the previous projection serves.
            logger.LogError(
                "Catalog file '{ConfiguredPath}' not found (probed from {BaseDirectory}); catalog sync skipped — the previous projection keeps serving.",
                options.Value.CatalogPath,
                AppContext.BaseDirectory);
            return;
        }

        try
        {
            var catalogJson = await File.ReadAllTextAsync(path, cancellationToken);

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<RegistryDbContext>();
            var outcome = await new CatalogSynchronizer(db, synchronizerLogger)
                .SyncAsync(catalogJson, cancellationToken);

            logger.LogInformation("Catalog sync from {Path}: {Outcome}.", path, outcome);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rejections are handled (and audited) inside the synchronizer; reaching here means an
            // infrastructure failure (file IO / database). Boot anyway — the projection is unchanged
            // and the api is still the run-tracking host (scale-to-zero wedge precedent, PR #39).
            logger.LogError(ex, "Catalog sync failed; the previous projection keeps serving.");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Resolves the configured path: absolute wins; a relative path is probed against
    /// <see cref="AppContext.BaseDirectory"/> and then each parent — so the same default works in
    /// the container (<c>/app/archetypes/catalog.json</c>) and under local dev/Aspire (repo root).
    /// </summary>
    private static string? ResolveCatalogPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configuredPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Options for <see cref="CatalogSyncService"/> (config section <c>Catalog</c>).</summary>
public sealed class CatalogSyncOptions
{
    /// <summary>The config section name.</summary>
    public const string SectionName = "Catalog";

    /// <summary>Path to the catalog file; relative paths are probed upward from the app base dir.</summary>
    public string CatalogPath { get; set; } = Path.Combine("archetypes", "catalog.json");
}
