namespace Pdp.ControlPlane.Inventory.Model;

/// <summary>
/// The typed, point-in-time answer to "what does PDP manage, and where?" — computed live from Azure
/// Resource Graph over the <c>pdp-*</c> tag schema, never from local records (Article III; data-model §5).
/// A pure projection: re-running recomputes from scratch, nothing is persisted. <see cref="Workloads"/>
/// is the flat list; the same items are also grouped under <see cref="Environments"/>.
/// </summary>
public sealed record InventorySnapshot(
    IReadOnlyList<EnvironmentView> Environments,
    IReadOnlyList<FabricItem> Fabrics,
    IReadOnlyList<PlatformItem> Platform,
    IReadOnlyList<SpokeItem> Spokes,
    IReadOnlyList<WorkloadItem> Workloads,
    IReadOnlyList<DriftFinding> Drift,
    IReadOnlyList<SubscriptionCoverage> Coverage);
