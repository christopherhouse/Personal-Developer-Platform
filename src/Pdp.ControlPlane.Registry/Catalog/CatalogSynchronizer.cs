using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Registry.Catalog;

/// <summary>
/// Projects one catalog file into the registry (spec 008, R1/R2 — contracts/archetype-catalog.md
/// §Sync semantics). Idempotent: an unchanged file hash short-circuits; changes apply
/// all-or-nothing (one <c>SaveChanges</c> transaction, audit row included). Rejections — an invalid
/// file, or content presented for an existing <c>(archetype, version)</c> under a different hash —
/// are <b>sync-fatal but boot-safe</b>: loud (error log + <c>catalog_syncs</c> row
/// <c>rejected</c>) while the previous projection keeps serving deploys.
/// </summary>
public sealed class CatalogSynchronizer(RegistryDbContext context, ILogger<CatalogSynchronizer> logger)
{
    private static readonly JsonSerializerOptions SummaryJson = new(JsonSerializerDefaults.Web);

    /// <summary>Runs one sync pass over the given catalog file content.</summary>
    public async Task<CatalogSyncOutcome> SyncAsync(string catalogJson, CancellationToken cancellationToken = default)
    {
        var contentHash = CatalogDefinition.Sha256Hex(catalogJson);

        // 1. No-change short-circuit: same content as the last successful sync.
        var lastSuccess = await context.CatalogSyncs
            .AsNoTracking()
            .Where(s => s.Outcome != CatalogSyncOutcome.Rejected)
            .OrderByDescending(s => s.AppliedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (lastSuccess?.ContentHash == contentHash)
        {
            return await RecordAsync(CatalogSyncOutcome.NoChange, contentHash, [], cancellationToken);
        }

        // 2. Validate (shape + every parameterSchema parses as a valid 2020-12 schema).
        CatalogDefinition definition;
        try
        {
            definition = CatalogDefinition.Parse(catalogJson);
        }
        catch (CatalogDefinitionException ex)
        {
            logger.LogError(
                "Catalog sync REJECTED — archetypes/catalog.json is invalid; keeping the previous projection. Violations: {Violations}",
                string.Join(" | ", ex.Violations));
            var rejections = ex.Violations.Select(v => new SyncAction(null, "rejected", null, v)).ToList();
            return await RecordAsync(CatalogSyncOutcome.Rejected, contentHash, rejections, cancellationToken);
        }

        // 3. Upsert: insert new archetypes/versions, apply status changes, enforce immutability.
        var actions = new List<SyncAction>();
        var violations = new List<SyncAction>();
        var now = DateTimeOffset.UtcNow;

        var existingArchetypes = await context.Archetypes
            .Include(a => a.Versions)
            .ToDictionaryAsync(a => a.Name, cancellationToken);

        foreach (var entry in definition.Archetypes)
        {
            if (!existingArchetypes.TryGetValue(entry.Name, out var archetype))
            {
                archetype = new Archetype
                {
                    Name = entry.Name,
                    Description = entry.Description,
                    Status = entry.Status,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                context.Archetypes.Add(archetype);
                actions.Add(new SyncAction(entry.Name, "added", null, null));
            }
            else
            {
                if (archetype.Status != entry.Status)
                {
                    actions.Add(new SyncAction(
                        entry.Name,
                        entry.Status == ArchetypeStatus.Retired ? "retired" : "reactivated",
                        null,
                        null));
                    archetype.Status = entry.Status;
                    archetype.UpdatedAt = now;
                }

                if (archetype.Description != entry.Description)
                {
                    archetype.Description = entry.Description;
                    archetype.UpdatedAt = now;
                }
            }

            foreach (var version in entry.Versions)
            {
                var existing = archetype.Versions.SingleOrDefault(v => v.Version == version.Version);
                if (existing is null)
                {
                    archetype.Versions.Add(new ArchetypeVersion
                    {
                        ArchetypeName = entry.Name,
                        Version = version.Version,
                        ModulePath = version.ModulePath,
                        ParameterSchema = version.ParameterSchemaJson,
                        ContentHash = version.ContentHash,
                        RegisteredAt = now,
                    });
                    actions.Add(new SyncAction(entry.Name, "version-added", version.Version, null));
                }
                else if (existing.ContentHash != version.ContentHash)
                {
                    // Version immutability (R2): a stamped tag's meaning can never drift.
                    violations.Add(new SyncAction(
                        entry.Name,
                        "rejected",
                        version.Version,
                        $"version is immutable once synced: content hash {version.ContentHash} differs from registered {existing.ContentHash}"));
                }
            }
        }

        // Entries removed from the file are intentionally left untouched — deployed workloads
        // reference stamped versions; retirement is the only supported off-ramp (contract).

        if (violations.Count > 0)
        {
            // Whole-sync failure: discard every pending upsert, keep the previous projection.
            context.ChangeTracker.Clear();
            logger.LogError(
                "Catalog sync REJECTED — version immutability violated; keeping the previous projection. Violations: {Violations}",
                string.Join(" | ", violations.Select(v => $"{v.Archetype}/{v.Version}: {v.Reason}")));
            return await RecordAsync(CatalogSyncOutcome.Rejected, contentHash, violations, cancellationToken);
        }

        // 4. Audit row in the same transaction as the upserts (one SaveChanges = all-or-nothing).
        AddAuditRow(CatalogSyncOutcome.Applied, contentHash, actions);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Catalog sync applied ({ActionCount} changes): {Actions}",
            actions.Count,
            string.Join(", ", actions.Select(a => $"{a.Archetype} {a.Action}{(a.Version is null ? "" : $" {a.Version}")}")));
        return CatalogSyncOutcome.Applied;
    }

    private async Task<CatalogSyncOutcome> RecordAsync(
        CatalogSyncOutcome outcome,
        string contentHash,
        IReadOnlyList<SyncAction> actions,
        CancellationToken cancellationToken)
    {
        AddAuditRow(outcome, contentHash, actions);
        await context.SaveChangesAsync(cancellationToken);
        return outcome;
    }

    private void AddAuditRow(CatalogSyncOutcome outcome, string contentHash, IReadOnlyList<SyncAction> actions) =>
        context.CatalogSyncs.Add(new CatalogSync
        {
            Id = Guid.CreateVersion7(),
            ContentHash = contentHash,
            Outcome = outcome,
            Summary = JsonSerializer.Serialize(actions, SummaryJson),
            AppliedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>One per-entry action in a sync's audit summary (FR-006).</summary>
    /// <param name="Archetype">The affected archetype (null for whole-file rejections).</param>
    /// <param name="Action">added | version-added | retired | reactivated | rejected.</param>
    /// <param name="Version">The affected version, when version-scoped.</param>
    /// <param name="Reason">Why, for rejections.</param>
    public sealed record SyncAction(string? Archetype, string Action, string? Version, string? Reason);
}
