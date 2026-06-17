using Pdp.ControlPlane.Inventory.Classification;
using Pdp.ControlPlane.Inventory.Model;

namespace Pdp.ControlPlane.Inventory;

/// <summary>
/// The inventory orchestrator (contracts §1): discover subscriptions → query ARG → classify → group →
/// drift → assemble an <see cref="InventorySnapshot"/>. Everything derives from the injected
/// <see cref="IResourceGraphReader"/> (the only Azure boundary); the service holds no Azure types and no
/// state. Taxonomy and grouping are computed only over <see cref="SubscriptionStatus.Queried"/>
/// subscriptions, with inaccessible ones preserved in <see cref="InventorySnapshot.Coverage"/> (FR-011).
/// The headline projections take the lighter taxonomy-only path (one ARG query, no invisible/drift work
/// they don't return) so a scoped query stays well inside its latency budget (SC-005).
/// </summary>
public sealed class InventoryService : IInventoryService
{
    private readonly IResourceGraphReader _reader;

    public InventoryService(IResourceGraphReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <inheritdoc />
    public async Task<InventorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var taxonomy = await BuildTaxonomyAsync(cancellationToken).ConfigureAwait(false);

        var queriedSubscriptionIds = taxonomy.Coverage
            .Where(c => c.Status == SubscriptionStatus.Queried)
            .Select(c => c.SubscriptionId)
            .ToList();

        var invisibleCandidates = await _reader.QueryLooksManagedUntaggedAsync(queriedSubscriptionIds, cancellationToken).ConfigureAwait(false);

        // Drift is informational: findings are pure data over the same rows; their presence never
        // fails the snapshot (FR-016).
        var drift = DriftDetector.Detect(taxonomy.ManagedRows, invisibleCandidates);

        return new InventorySnapshot(
            taxonomy.Environments,
            taxonomy.Fabrics,
            taxonomy.Platform,
            taxonomy.Spokes,
            taxonomy.Workloads,
            drift,
            taxonomy.Coverage);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentView>> GetEnvironmentsAsync(CancellationToken cancellationToken = default) =>
        (await BuildTaxonomyAsync(cancellationToken).ConfigureAwait(false)).Environments;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SpokeItem>> GetSpokesAsync(CancellationToken cancellationToken = default) =>
        (await BuildTaxonomyAsync(cancellationToken).ConfigureAwait(false)).Spokes;

    /// <inheritdoc />
    public async Task<EnvironmentView?> GetEnvironmentAsync(string name, CancellationToken cancellationToken = default)
    {
        var taxonomy = await BuildTaxonomyAsync(cancellationToken).ConfigureAwait(false);
        // Unknown environment → null (a clean empty result, not an error — FR-008).
        return taxonomy.Environments.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The shared core: discover subscriptions and classify the managed RGs into the taxonomy. This is
    /// the only path the headline projections need; <see cref="GetSnapshotAsync"/> layers the
    /// invisible-drift query and drift detection on top, reusing <see cref="TaxonomyResult.ManagedRows"/>.
    /// </summary>
    private async Task<TaxonomyResult> BuildTaxonomyAsync(CancellationToken cancellationToken)
    {
        var coverage = await _reader.DiscoverSubscriptionsAsync(cancellationToken).ConfigureAwait(false);

        var queriedSubscriptionIds = coverage
            .Where(c => c.Status == SubscriptionStatus.Queried)
            .Select(c => c.SubscriptionId)
            .ToList();

        var rows = await _reader.QueryManagedResourceGroupsAsync(queriedSubscriptionIds, cancellationToken).ConfigureAwait(false);

        var fabrics = new List<FabricItem>();
        var platform = new List<PlatformItem>();
        var spokes = new List<SpokeItem>();
        var workloads = new List<WorkloadItem>();

        foreach (var row in rows)
        {
            // Belt-and-braces: the owner-managed seed backend is never classified (FR-018). The KQL
            // already excludes it (untagged), but guard here too in case it is ever hand-tagged.
            if (TagSchema.IsSeedBackend(row.Name))
            {
                continue;
            }

            var result = ResourceGroupClassifier.Classify(row);
            switch (result.Kind)
            {
                case ClassificationKind.Fabric:
                    fabrics.Add(result.Fabric!);
                    break;
                case ClassificationKind.Platform:
                    platform.Add(result.Platform!);
                    break;
                case ClassificationKind.Spoke:
                    spokes.Add(result.Spoke!);
                    break;
                case ClassificationKind.Workload:
                    workloads.Add(result.Workload!);
                    break;
                case ClassificationKind.Orphan:
                case ClassificationKind.Ambiguous:
                    // No taxonomy item — surfaced as a drift finding by GetSnapshotAsync.
                    break;
            }
        }

        return new TaxonomyResult(
            GroupByEnvironment(workloads),
            fabrics.OrderBy(f => f.Region, StringComparer.OrdinalIgnoreCase).ToList(),
            platform.OrderBy(p => p.ResourceGroupName, StringComparer.OrdinalIgnoreCase).ToList(),
            spokes.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            workloads.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            coverage,
            rows);
    }

    /// <summary>
    /// Groups workloads carrying a <c>pdp-env</c> into environments; workloads missing the tag are
    /// excluded here (they remain in the flat list and raise a conformance finding — data-model §6).
    /// </summary>
    private static IReadOnlyList<EnvironmentView> GroupByEnvironment(IEnumerable<WorkloadItem> workloads) =>
        workloads
            .Where(w => !string.IsNullOrEmpty(w.Environment))
            .GroupBy(w => w.Environment!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new EnvironmentView(
                g.Key,
                g.OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

    /// <summary>The classified taxonomy plus the managed rows (reused for drift) and coverage.</summary>
    private sealed record TaxonomyResult(
        IReadOnlyList<EnvironmentView> Environments,
        IReadOnlyList<FabricItem> Fabrics,
        IReadOnlyList<PlatformItem> Platform,
        IReadOnlyList<SpokeItem> Spokes,
        IReadOnlyList<WorkloadItem> Workloads,
        IReadOnlyList<SubscriptionCoverage> Coverage,
        IReadOnlyList<ResourceGroupRow> ManagedRows);
}
